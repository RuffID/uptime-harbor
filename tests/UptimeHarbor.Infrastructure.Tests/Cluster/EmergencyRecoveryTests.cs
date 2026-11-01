using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Models;
using UptimeHarbor.Infrastructure.Identity;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class EmergencyRecoveryTests
{
    [Fact]
    public async Task QuarantinedSnapshotInitializesRealFreshWalWithHistoricalTermFloorWithoutOpeningWork()
    {
        // Arrange
        using Fixture fixture = new();
        await fixture.PrepareSourceAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        PublishedRevisionSnapshot prepared = await fixture.Recovery.PrepareAsync(fixture.Selection!, fixture.Target,
            fixture.Node!, fixture.Credential!, Fixture.PASSWORD, token);
        File.WriteAllBytes(Path.Combine(fixture.Target.StoragePath, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(
            new ClusterDiskManifest(fixture.Target.ClusterId, fixture.Target.Generation, fixture.Target.NodeId,
                fixture.Target.StorageGeneration, fixture.Target.PublicUri, Guid.NewGuid()) { RecoveryId = fixture.Selection!.RecoveryId }));
        RecoveryAdmission proof = new(fixture.Target.ClusterId, fixture.Target.Generation, fixture.Target.NodeId,
            fixture.Target.StorageGeneration, true, true, prepared.Manifest.SnapshotId, prepared.Digest, prepared.Image.Index, 0)
        {
            Operations = prepared.Image.Operations, MonitoringNodes = prepared.Image.Monitoring.Nodes
        };
        using ServiceProvider services = new ServiceCollection().AddSingleton<IPendingControlUnitOfWork>(new PendingMemory()).BuildServiceProvider();
        RevisionStateStore state = new();
        RevisionRecoveryTests.Projection projection = new(new(fixture.Target.ClusterId, fixture.Target.Generation));
        RevisionClusterRecovery recovery = new(new Admission(proof), state, new(state, new(), projection), projection,
            new(Options.Create(fixture.Target)), services.GetRequiredService<IServiceScopeFactory>(), Options.Create(fixture.Target));
        NodeReadiness readiness = new(fixture.Time);
        ClusterStateMachine machine = new(recovery, readiness);

        // Act
        Assert.True(await machine.RestoreAsync(fixture.Target, token));
        await using WriteAheadLog wal = new(new WriteAheadLog.Options
        {
            Location = Path.Combine(fixture.Target.StoragePath, "wal"), FlushInterval = TimeSpan.Zero,
            FlushOnCommit = true, HashAlgorithm = WriteAheadLog.IntegrityHashAlgorithm.Crc64
        }, machine);
        await wal.InitializeAsync(token);
        await ((IPersistentState)wal).UpdateTermAsync(prepared.Image.Term, true, token);
        Assert.True(recovery.VerifyNativeMembership(0, new HashSet<Guid> { fixture.Target.NodeId }, false));
        bool confirmed = await recovery.ConfirmLocalReplayAsync(wal.LastCommittedEntryIndex, wal.LastAppliedIndex, 0, token);

        // Assert
        Assert.True(confirmed);
        Assert.Equal(prepared.Image.Term, ((IPersistentState)wal).Term);
        Assert.True(wal.LastCommittedEntryIndex >= prepared.Image.Index);
        Assert.False(recovery.Accepted.Activated);
        Assert.False(recovery.Accepted.ManuallyEnabled);
        Assert.Equal(fixture.Selection.RecoveryId, state.Current!.Operations.MaintenanceId);
        AssertSameContent(prepared.Image.Monitoring.Attempts, state.Current.Monitoring.Attempts);
    }

    private class Admission(RecoveryAdmission proof) : IRecoveryAdmission
    {
        public ValueTask<RecoveryAdmission?> ReadAsync(CancellationToken token)
        {
            return ValueTask.FromResult<RecoveryAdmission?>(proof);
        }
    }

    [Fact]
    public async Task UnknownHistoricalAttemptCanBeReconciledWithoutRewritingItsOriginalAuthority()
    {
        // Arrange
        using Fixture fixture = new();
        await fixture.PrepareSourceAsync(unknown: true);
        PublishedRevisionSnapshot prepared = await fixture.Recovery.PrepareAsync(fixture.Selection!, fixture.Target,
            fixture.Node!, fixture.Credential!, Fixture.PASSWORD, TestContext.Current.CancellationToken);
        RevisionImage baseline = prepared.Image;
        AcceptedDeliveryAttempt original = Assert.Single(baseline.Monitoring.Attempts.Values);
        SenderAuthorityKey authority = new(baseline.ClusterId, baseline.Generation, 0, baseline.Term + 1, fixture.Target.NodeId, Guid.NewGuid());
        RevisionCommand abandon = DeliveryCommands.Create(baseline, fixture.Target.NodeId, fixture.Target.StorageGeneration,
            "AbandonDelivery", original.IntentId, Guid.NewGuid(), fixture.Time.Now.AddMinutes(1),
            new DeliveryCommand { AttemptId = original.Id, Authority = authority });

        // Act
        RevisionImage reconciled = new RevisionReducer().Apply(baseline, abandon, baseline.Index + 1, baseline.Term + 1);
        DeliveryQueue.Validate(reconciled);

        // Assert
        Assert.Equal(UptimeHarbor.Domain.Cluster.OperationOutcome.Committed, reconciled.Results[abandon.OperationId].Outcome);
        Assert.Equal(original.Authority, reconciled.Monitoring.Attempts[original.Id].Authority);
        Assert.Equal("Ambiguous", reconciled.Monitoring.Attempts[original.Id].FailureKind);
        Assert.Equal(UptimeHarbor.Domain.Cluster.DeliveryState.RetryDue, reconciled.Monitoring.Intents[original.IntentId].State);
    }

    [Fact]
    public async Task DeclinedLossConfirmationCannotCreateNewGrid()
    {
        // Arrange
        using Fixture fixture = new();
        await fixture.PrepareSourceAsync();
        EmergencyRecoverySelection selection = fixture.Selection! with { LossAcknowledgement = "declined" };

        // Act
        Exception? failure = await Record.ExceptionAsync(() => fixture.Recovery.PrepareAsync(selection,
            fixture.Target, fixture.Node!, fixture.Credential!, Fixture.PASSWORD, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidDataException>(failure);
        Assert.False(Directory.Exists(fixture.Target.StoragePath));
        string audit = await File.ReadAllTextAsync(selection.SnapshotPath + ".recovery-audit.jsonl", TestContext.Current.CancellationToken);
        Assert.Contains("DeclinedOrRejected", audit);
        Assert.DoesNotContain(Fixture.PASSWORD, audit);
    }

    [Fact]
    public async Task SelectedLaggingCopyPreservesRealDeliveryAndAccountsButNotMissingChangesOrOldVotes()
    {
        // Arrange
        using Fixture fixture = new();
        await fixture.PrepareSourceAsync();
        RevisionImage source = fixture.Source!;

        // Act
        PublishedRevisionSnapshot prepared = await fixture.Recovery.PrepareAsync(fixture.Selection!,
            fixture.Target, fixture.Node!, fixture.Credential!, Fixture.PASSWORD, TestContext.Current.CancellationToken);
        RevisionImage baseline = prepared.Image;
        RevisionSnapshotStore target = new(Options.Create(fixture.Target));
        PublishedRevisionSnapshot reread = await target.ReadAsync(prepared.Path, prepared.Digest, TestContext.Current.CancellationToken);

        // Assert
        AssertSameContent(source.Values, baseline.Values);
        AssertSameContent(source.Results, baseline.Results);
        AssertSameContent(source.Controls, baseline.Controls);
        AssertSameContent(source.Monitoring.Attempts, baseline.Monitoring.Attempts);
        AssertSameContent(source.Monitoring.Intents, baseline.Monitoring.Intents);
        AssertSameContent(source.Monitoring.ConfirmedDeliveries, baseline.Monitoring.ConfirmedDeliveries);
        AssertSameContent(source.Monitoring.Observations, baseline.Monitoring.Observations);
        Assert.Equal(source.Index, baseline.Index);
        Assert.Equal(source.Index + 7, baseline.RecoveryBaseline!.KnownCommittedIndex);
        Assert.Equal(source.ClusterId, baseline.RecoveryBaseline.PreviousClusterId);
        Assert.NotEqual(source.ClusterId, baseline.ClusterId);
        Assert.Null(baseline.Operations.Sender);
        Assert.Null(baseline.BootstrapAccountDigest);
        Assert.Equal(fixture.Selection!.RecoveryId, baseline.Operations.MaintenanceId);
        OperationalNode voter = Assert.Single(baseline.Operations.Nodes.Values, node => node.Voting);
        Assert.Equal(fixture.Target.NodeId, voter.NodeId);
        Assert.False(voter.Activated);
        Assert.All(source.Operations.Nodes.Keys, id => Assert.True(baseline.Operations.Nodes[id].Removed));
        Assert.Equal(baseline.ClusterId, reread.Image.ClusterId);
        DeliveryQueue.Validate(reread.Image);
        Assert.Empty(DeliveryQueue.Due(reread.Image, fixture.Time.Now, 100));
        string audit = await File.ReadAllTextAsync(Path.Combine(fixture.Target.StoragePath, "recovery-audit.jsonl"), TestContext.Current.CancellationToken);
        Assert.Contains("SelectedAndConfirmed", audit);
        Assert.Contains("Quarantined", audit);
        Assert.DoesNotContain(Fixture.PASSWORD, audit);
        Assert.DoesNotContain("PasswordHash", audit);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("missing-node")]
    [InlineData("token")]
    [InlineData("old-identity")]
    [InlineData("cut")]
    public async Task MissingPrerequisiteKeepsDisasterRecoveryClosed(string missing)
    {
        // Arrange
        using Fixture fixture = new();
        await fixture.PrepareSourceAsync();
        EmergencyRecoverySelection selection = fixture.Selection!;
        string password = Fixture.PASSWORD;
        if (missing == "password")
        {
            password = "wrong";
        }
        else if (missing == "missing-node")
        {
            selection = selection with { Isolation = selection.Isolation with { Instances = selection.Isolation.Instances.Take(1).ToArray() } };
        }
        else if (missing == "token")
        {
            selection = selection with { Isolation = selection.Isolation with { PreviousBotTokenRevoked = false } };
        }
        else if (missing == "old-identity")
        {
            fixture.Target.NodeId = fixture.Source!.Operations.Nodes.Keys.First();
        }
        else
        {
            selection = selection with { SourceIndex = selection.SourceIndex + 1 };
        }

        // Act
        Exception? failure = await Record.ExceptionAsync(() => fixture.Recovery.PrepareAsync(selection,
            fixture.Target, fixture.Node!, fixture.Credential!, password, TestContext.Current.CancellationToken));

        // Assert
        Assert.NotNull(failure);
        Assert.False(Directory.Exists(fixture.Target.StoragePath));
    }

    [Fact]
    public async Task LateFormerNodesCannotAuthenticateToNewGridOrRegainTrust()
    {
        // Arrange
        using Fixture fixture = new();
        await fixture.PrepareSourceAsync();
        PublishedRevisionSnapshot prepared = await fixture.Recovery.PrepareAsync(fixture.Selection!, fixture.Target,
            fixture.Node!, fixture.Credential!, Fixture.PASSWORD, TestContext.Current.CancellationToken);
        RevisionStateStore state = new();
        state.Publish(prepared.Image);
        AcceptedPeerTrustSource trust = new(state, fixture.Time);
        PeerProtocolGuard guard = new(fixture.Target, trust, fixture.Time);
        OperationalCredential old = fixture.Source!.Operations.Credentials.Values.First();
        OperationalNode oldNode = fixture.Source.Operations.Nodes[old.NodeId];
        PeerEnvelope envelope = new(fixture.Source.ClusterId, fixture.Source.Generation, old.NodeId, fixture.Target.NodeId,
            oldNode.StorageGeneration, old.Id, Guid.NewGuid(), fixture.Time.Now, fixture.Target.Compatibility, Guid.NewGuid());

        // Act
        PeerCredential? allowed = await guard.ValidateAsync(envelope, 0, 65536, TestContext.Current.CancellationToken);
        PeerCredential? oldTrust = await trust.FindAsync(old.Id, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(allowed);
        Assert.Null(oldTrust);
        Assert.Single(await trust.ListAsync(TestContext.Current.CancellationToken));
        Assert.False(prepared.Image.Operations.Nodes[old.NodeId].Voting);
    }

    private static void AssertSameContent<T>(T expected, T actual)
    {
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected), JsonSerializer.SerializeToElement(actual)));
    }

    private class Fixture : IDisposable
    {
        public const string PASSWORD = "Admin-Test-123!";
        private readonly ClusterTestDirectory directory = new();
        private readonly DeliveryHarness delivery = new();
        public MonitoringHarness.Clock Time { get; } = new() { Now = DateTimeOffset.UtcNow };
        public ClusterOptions Target { get; }
        public RevisionImage? Source { get; private set; }
        public EmergencyRecoverySelection? Selection { get; private set; }
        public OperationalNode? Node { get; private set; }
        public OperationalCredential? Credential { get; private set; }
        public EmergencyRecoveryOrchestrator Recovery { get; }

        public Fixture()
        {
            Target = new()
            {
                Mode = "Bootstrap", ClusterId = Guid.NewGuid(), Generation = 2, NodeId = Guid.NewGuid(), StorageGeneration = Guid.NewGuid(),
                CredentialId = Guid.NewGuid(), StoragePath = Path.Combine(directory.Location, "target"),
                PublicUri = "https://new.invalid/internal/raft", ExpectedBotId = 123, SnapshotChunkMaxBytes = 16384
            };
            Recovery = new(new PasswordHasher<ApplicationUser>(), Time);
        }

        public async Task PrepareSourceAsync(bool unknown = false)
        {
            await delivery.PrepareAsync();
            delivery.RejectResults = unknown;
            await delivery.Delivery.RunOnceAsync(TestContext.Current.CancellationToken);
            if (unknown)
            {
                Assert.Empty(delivery.Image.Monitoring.ConfirmedDeliveries);
                Assert.Equal(UptimeHarbor.Domain.Cluster.AttemptOutcome.Unknown, Assert.Single(delivery.Image.Monitoring.Attempts.Values).Outcome);
            }
            else
            {
                Assert.Single(delivery.Image.Monitoring.ConfirmedDeliveries);
            }
            RevisionImage image = delivery.Image;
            ClusterOperationsImage operations = new() { BotId = 123, Revision = image.Index };
            ImmutableDictionary<Guid, Guid> nodes = image.Monitoring.Nodes;
            while (nodes.Count < 3)
            {
                nodes = nodes.Add(Guid.NewGuid(), Guid.NewGuid());
            }

            int number = 0;
            foreach (KeyValuePair<Guid, Guid> item in nodes)
            {
                number++;
                OperationalNode node = new(item.Key, item.Value, $"https://old-{number}.invalid/internal/raft", item.Key.ToString("N"), true, true, false, image.Index);
                OperationalCredential credential = new(Guid.NewGuid(), node.NodeId, new string((char)('A' + number), 64),
                    "public certificate", Time.Now.AddYears(1), null, null);
                operations = operations with { Nodes = operations.Nodes.Add(node.NodeId, node), Credentials = operations.Credentials.Add(credential.Id, credential) };
            }

            AcceptedValue actor = image.Values[delivery.Actor];
            PasswordHasher<ApplicationUser> hasher = new();
            actor = actor with { Value = actor.Value with { PasswordHash = hasher.HashPassword(new(actor.Id, actor.Value.Name!), PASSWORD) } };
            image = image with { Values = image.Values.SetItem(actor.Id, actor), Operations = operations, Monitoring = image.Monitoring with { Nodes = nodes } };
            image = MonitoringSnapshotRows.Export(image);
            using var context = RevisionFixture.CreateContext();
            var rows = image.ProjectionRows.Where(row => row.Entity != typeof(UptimeHarbor.Domain.Cluster.ClusterMember).FullName).ToImmutableArray();
            rows = rows.AddRange(ClusterOperationsProjection.Changes(new(image.ClusterId, image.Generation), image)
                .Select(record => SnapshotSchema.Capture(context, context.Model.FindEntityType(record.GetType())!, record)));
            Source = image with { ProjectionRows = rows };
            SnapshotImageValidation.Validate(context, Source);
            RevisionSnapshotStore writer = new(Options.Create(new ClusterOptions
            {
                ClusterId = Source.ClusterId, Generation = Source.Generation,
                StoragePath = Path.Combine(directory.Location, "source"), SnapshotChunkMaxBytes = 16384
            }));
            PublishedRevisionSnapshot selected = await writer.PublishAsync(Source, TestContext.Current.CancellationToken);
            ManualIsolationConfirmation isolation = new(Guid.NewGuid(), Guid.NewGuid(), Time.Now,
                nodes.Select(item => new IsolatedClusterInstance(item.Key, item.Value, true, true, true, true, true, new string('F', 64))).ToArray(),
                123, true, true, new string('E', 64));
            Selection = new(Guid.NewGuid(), actor.Id, Source.ClusterId, Source.Generation, selected.Path, selected.Digest,
                Source.Index, Source.Term, Source.Index + 7, EmergencyRecoveryOrchestrator.LOSS_ACKNOWLEDGEMENT, isolation);
            Node = new(Target.NodeId, Target.StorageGeneration, Target.PublicUri, Target.NodeId.ToString("N"), true, false, false, 0);
            Credential = new(Target.CredentialId, Target.NodeId, new string('A', 64), "public certificate", Time.Now.AddYears(1), null, null);
        }

        public void Dispose()
        {
            delivery.Dispose();
            directory.Dispose();
        }
    }
}
