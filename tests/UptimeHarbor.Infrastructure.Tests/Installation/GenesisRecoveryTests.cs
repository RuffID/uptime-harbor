using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Tests.Infrastructure.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class GenesisRecoveryTests
{
    [Fact]
    public async Task TypedLocalGenesisRestoresEmptyWalBeforeAccountAndReplaysInitializeThenAccountInQuarantine()
    {
        // Arrange
        using GenesisFixture fixture = new();
        SetupAuthorityEvidence evidence = await fixture.PrepareAsync();
        await fixture.SaveAsync(evidence);
        GenesisRuntimePreparation runtime = fixture.Runtime();
        await runtime.PrepareAsync(fixture.Local.Store.Profile!, TestContext.Current.CancellationToken);
        ClusterOptions options = fixture.Local.Cluster;
        options.Mode = "Bootstrap";
        options.ClusterId = fixture.Account.ClusterId;
        options.Generation = 1;
        options.NodeId = fixture.Account.OriginNodeId;
        options.StorageGeneration = fixture.Local.Store.Profile!.StorageGeneration!.Value;
        options.PublicUri = fixture.Local.Store.Profile.PeerAddress!;
        options.ProtocolMajor = 3;
        options.CommandSchema = 3;
        options.SnapshotFormat = 3;
        options.SqlSchema = 3;
        NodeReadiness readiness = new(fixture.Local.Time);
        ClusterLocalControl control = new(Options.Create(options), Options.Create(new ClusterControlOptions()), fixture.Local.Time, readiness);
        ManagedRecoveryAdmission source = new(Options.Create(fixture.Local.Options), fixture.Admission,
            new SignedRecoveryAdmission(control, Options.Create(options), fixture.Local.Time));
        RevisionRecoveryTests.Projection projection = new(new(options.ClusterId, 1));
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        RevisionClusterRecovery recovery = new(source, fixture.Accepted,
            new(fixture.Accepted, new(), projection), projection, new(Options.Create(options)),
            services.GetRequiredService<IServiceScopeFactory>(), Options.Create(options));
        ClusterStateMachine machine = new(recovery, readiness);
        CancellationToken token = TestContext.Current.CancellationToken;

        // Act
        bool restored = await machine.RestoreAsync(options, token);
        await using WriteAheadLog wal = new(new WriteAheadLog.Options
        {
            Location = Path.Combine(options.StoragePath, "wal"), FlushOnCommit = true,
            FlushInterval = TimeSpan.Zero, HashAlgorithm = WriteAheadLog.IntegrityHashAlgorithm.Crc64
        }, machine);
        await wal.InitializeAsync(token);
        bool singleton = recovery.VerifyNativeMembership(17, new HashSet<Guid> { options.NodeId }, false);
        bool emptyReplay = await recovery.ConfirmLocalReplayAsync(0, 0, 17, token);

        // Assert
        Assert.True(restored);
        Assert.True(singleton);
        Assert.True(emptyReplay);
        Assert.Equal(options.NodeId, recovery.ResolveNativeVoter(options.PublicUri));
        Assert.Null(recovery.ResolveNativeVoter("https://foreign.example.test/internal/raft"));
        Assert.True(recovery.Accepted.CaughtUp);
        Assert.False(recovery.Accepted.AccountStateConfirmed);
        Assert.Empty(fixture.Accepted.Current!.Operations.Credentials);

        // Act
        RevisionCommand initialize = evidence.Genesis!.InitializeCluster with
        {
            EnrollmentTrust = evidence.Genesis.InitializeCluster.EnrollmentTrust! with
            {
                CoordinatorAtUtc = fixture.Local.Time.Now, CoordinatorTerm = 1, GenesisMembershipVersion = 17
            }
        };
        foreach (RevisionCommand command in new[] { initialize, fixture.Account })
        {
            long index = await wal.AppendAsync(new BinaryLogEntry
            {
                Term = 1, CommandId = RevisionCodec.COMMAND_ID, Content = RevisionCodec.Encode(command)
            }, token);
            await wal.CommitAsync(index, token);
            await wal.WaitForApplyAsync(index, token);
        }

        bool replayed = await recovery.ConfirmLocalReplayAsync(wal.LastCommittedEntryIndex, wal.LastAppliedIndex, 17, token);

        // Assert
        Assert.True(replayed);
        Assert.Equal(options.NodeId, recovery.ResolveNativeVoter(options.PublicUri));
        Assert.True(recovery.Accepted.AccountStateConfirmed);
        Assert.Single(fixture.Accepted.Current!.Values);
        Assert.Single(fixture.Accepted.Current.Operations.Credentials);
        Assert.False(recovery.Accepted.Activated);
        Assert.False(recovery.Accepted.ManuallyEnabled);
        Assert.Equal(fixture.Account.Value!.PasswordHash, fixture.Accepted.Current.Values[fixture.Account.EntityId].Value.PasswordHash);
        Assert.NotNull(fixture.Accepted.Find(fixture.Account.EntityId));
        Assert.False(fixture.Local.State.CommonOpen);
    }
}
