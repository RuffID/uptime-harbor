using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Persistence;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class OnboardingStorageTests
{
    [Fact]
    public void MigrationAddsOnlyInvitationProjectionAndMatchesCurrentModelWithoutConnection()
    {
        // Arrange
        using UptimeHarborDbContext context = RevisionFixture.CreateContext();
        IMigrationsAssembly migrations = context.GetService<IMigrationsAssembly>();
        var entry = Assert.Single(migrations.Migrations, item => item.Key.EndsWith("_AddOnboardingInvitations", StringComparison.Ordinal));
        Migration migration = migrations.CreateMigration(entry.Value, context.Database.ProviderName!);
        IModel snapshot = context.GetService<IModelRuntimeInitializer>().Initialize(migrations.ModelSnapshot!.Model, designTime: true);

        // Act
        CreateTableOperation table = Assert.Single(migration.UpOperations.OfType<CreateTableOperation>());
        bool differs = context.GetService<IMigrationsModelDiffer>().HasDifferences(snapshot.GetRelationalModel(), context.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        // Assert
        Assert.False(differs);
        Assert.Equal("onboarding_invitations", table.Name);
        Assert.Equal(2, migration.UpOperations.Count);
        Assert.IsType<DropTableOperation>(Assert.Single(migration.DownOperations));
        Assert.Contains(table.CheckConstraints, item => item.Name == "ck_onboarding_invitations_status");
        Assert.True(context.Model.FindEntityType(typeof(OnboardingProjection))!.FindProperty("LogIndex")!.IsConcurrencyToken);
        Assert.DoesNotContain(table.Columns, column => column.Name.Contains("Code", StringComparison.Ordinal));
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task SnapshotRoundTripPreservesRevocationAndChecksProjectionPayload()
    {
        // Arrange
        using ClusterTestDirectory directory = new();
        OnboardingStorageFixture fixture = new();
        RevisionImage revoked = new RevisionReducer().Apply(fixture.Image, fixture.Revoke(), 4, 2);
        ClusterOptions options = fixture.Options(directory.Location);
        RevisionSnapshotStore snapshots = new(Microsoft.Extensions.Options.Options.Create(options));
        RevisionImage image = fixture.WithRows(revoked);
        using UptimeHarborDbContext context = RevisionFixture.CreateContext();

        // Act
        SnapshotImageValidation.Validate(context, image);
        var published = await snapshots.PublishAsync(image, TestContext.Current.CancellationToken);
        var loaded = await snapshots.LoadAsync(published.Manifest.SnapshotId, published.Digest, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(InvitationStatus.Revoked, loaded.Image.Onboarding!.Invitations[fixture.InvitationId].Status);
        Assert.Equal(image.Onboarding!.Requests, loaded.Image.Onboarding.Requests);
        Assert.False(context.Database.GetDbConnection().State == System.Data.ConnectionState.Open);
        SharedSnapshotRow row = Assert.Single(image.ProjectionRows, item => item.Entity == typeof(OnboardingProjection).FullName);
        RevisionImage corrupt = image with { ProjectionRows = image.ProjectionRows.Replace(row, row with
        {
            Fields = row.Fields.SetItem("Payload", System.Text.Json.JsonSerializer.SerializeToElement("{}"))
        }) };
        Assert.Throws<InvalidDataException>(() => SnapshotImageValidation.Validate(context, corrupt));
    }

    [Fact]
    public async Task SnapshotAndRealWalReplayAfterRestartKeepOriginalDecisionAndNeverReviveRevokedCode()
    {
        // Arrange
        using ClusterTestDirectory directory = new();
        OnboardingStorageFixture fixture = new();
        ClusterOptions options = fixture.Options(directory.Location);
        RevisionSnapshotStore snapshots = new(Microsoft.Extensions.Options.Options.Create(options));
        var snapshot = await snapshots.PublishAsync(fixture.WithRows(fixture.Image), TestContext.Current.CancellationToken);
        RecoveryAdmission proof = new(options.ClusterId, 1, options.NodeId, options.StorageGeneration, true, false,
            snapshot.Manifest.SnapshotId, snapshot.Digest, fixture.Image.Index, 1)
        {
            MonitoringNodes = fixture.Image.Monitoring.Nodes, Operations = fixture.Image.Operations
        };
        RevisionRecoveryTests.Projection projection = new(fixture.Image);
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        RevisionCommand revoke = fixture.Revoke();
        string walPath = Path.Combine(directory.Location, "wal");

        // Act
        var first = Runtime(proof, projection, snapshots, services.GetRequiredService<IServiceScopeFactory>(), options);
        await first.Machine.RestoreAsync(options, TestContext.Current.CancellationToken);
        await using (WriteAheadLog wal = new(WalOptions(walPath), first.Machine))
        {
            await wal.InitializeAsync(TestContext.Current.CancellationToken);
            foreach (RevisionCommand command in new[] { revoke, revoke })
            {
                long index = await wal.AppendAsync(new BinaryLogEntry
                {
                    Term = 2, CommandId = RevisionCodec.COMMAND_ID, Content = RevisionCodec.Encode(command)
                }, TestContext.Current.CancellationToken);
                await wal.CommitAsync(index, TestContext.Current.CancellationToken);
                await wal.WaitForApplyAsync(index, TestContext.Current.CancellationToken);
            }
        }

        var restarted = Runtime(proof, projection, snapshots, services.GetRequiredService<IServiceScopeFactory>(), options);
        await restarted.Machine.RestoreAsync(options, TestContext.Current.CancellationToken);
        await using WriteAheadLog reopened = new(WalOptions(walPath), restarted.Machine);
        await reopened.InitializeAsync(TestContext.Current.CancellationToken);
        bool confirmed = await restarted.Recovery.ConfirmLocalReplayAsync(reopened.LastCommittedEntryIndex, reopened.LastAppliedIndex,
            1, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(confirmed);
        Assert.Equal(InvitationStatus.Revoked, restarted.State.Current!.Onboarding!.Invitations[fixture.InvitationId].Status);
        Assert.Equal(fixture.Image.Results[fixture.InvitationId], restarted.State.Current.Results[fixture.InvitationId]);
        Assert.Equal(2, restarted.State.Current.Onboarding.Requests.Count);
        Assert.False(restarted.Recovery.Accepted.Activated);
        Assert.False(restarted.Recovery.Accepted.ManuallyEnabled);
    }

    [Fact]
    public async Task OldCompatibilityRejectsNewCommandAndSnapshotBeforeMutation()
    {
        // Arrange
        using ClusterTestDirectory directory = new();
        OnboardingStorageFixture fixture = new();
        ClusterOptions old = new() { Mode = "Restart", StoragePath = directory.Location };
        byte[] batch = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new[] { fixture.Revoke() });
        RevisionSnapshotStore snapshots = new(Microsoft.Extensions.Options.Options.Create(old));

        // Act
        Exception? command = Record.Exception(() => RevisionBatchCodec.Decode(batch, old));
        Exception? snapshot = await Record.ExceptionAsync(() => snapshots.PublishAsync(fixture.Image, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidDataException>(command);
        Assert.IsType<InvalidDataException>(snapshot);
        Assert.Empty(Directory.GetFileSystemEntries(directory.Location));
    }

    [Fact]
    public async Task ProductionAuthorityCannotGrantAdmissionAndStreamCodecRejectsDuplicateFields()
    {
        // Arrange
        OnboardingStorageFixture fixture = new();
        UnavailableEnrollmentAuthority authority = new();
        using MemoryStream json = new("{\"Status\":\"Revoked\",\"Status\":\"Unused\"}"u8.ToArray());

        // Act
        bool authorized = await authority.VerifyAsync(fixture.Revoke(), TestContext.Current.CancellationToken);
        Exception? duplicate = await Record.ExceptionAsync(() => StrictSnapshotJson.ValidateAsync(json, TestContext.Current.CancellationToken));

        // Assert
        Assert.False(authorized);
        Assert.IsType<InvalidDataException>(duplicate);
    }

    private static (RevisionStateStore State, RevisionClusterRecovery Recovery, ClusterStateMachine Machine) Runtime(
        RecoveryAdmission proof, IRevisionProjectionStore projection, RevisionSnapshotStore snapshots, IServiceScopeFactory scopes, ClusterOptions options)
    {
        RevisionStateStore state = new();
        RevisionClusterRecovery recovery = new(new Admission(proof), state, new RevisionApplyEngine(state, new RevisionReducer(), projection),
            projection, snapshots, scopes, Microsoft.Extensions.Options.Options.Create(options));
        return (state, recovery, new ClusterStateMachine(recovery, new NodeReadiness(TimeProvider.System)));
    }

    private static WriteAheadLog.Options WalOptions(string path)
    {
        return new()
        {
            Location = path, FlushInterval = TimeSpan.Zero, FlushOnCommit = true, HashAlgorithm = WriteAheadLog.IntegrityHashAlgorithm.Crc64
        };
    }

    private class Admission(RecoveryAdmission proof) : IRecoveryAdmission
    {
        public ValueTask<RecoveryAdmission?> ReadAsync(CancellationToken token)
        {
            return ValueTask.FromResult<RecoveryAdmission?>(proof);
        }
    }
}
