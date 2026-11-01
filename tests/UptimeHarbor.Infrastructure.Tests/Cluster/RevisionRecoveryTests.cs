using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Models;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Domain.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class RevisionRecoveryTests
{
    [Fact]
    public async Task UnrelatedNonemptyJoinProjectionIsRejectedWithoutInstallOrAccountAccess()
    {
        // Arrange
        using ClusterTestDirectory directory = new();
        RevisionFixture fixture = new();
        ClusterOptions options = fixture.Options(directory.Location);
        options.Mode = "Join";
        RevisionSnapshotStore snapshots = new(Options.Create(options));
        PublishedRevisionSnapshot snapshot = await snapshots.PublishAsync(fixture.WithProjectionRows(), TestContext.Current.CancellationToken);
        RecoveryAdmission proof = new(options.ClusterId, 1, options.NodeId, options.StorageGeneration,
            true, false, snapshot.Manifest.SnapshotId, snapshot.Digest, snapshot.Image.Index, 1);
        Projection foreign = new(fixture.Baseline with { ClusterId = Guid.NewGuid() });
        using ServiceProvider scopes = new ServiceCollection().BuildServiceProvider();
        var runtime = Create(proof, foreign, snapshots, scopes.GetRequiredService<IServiceScopeFactory>(), options);

        // Act
        Exception? failure = await Record.ExceptionAsync(async () => await runtime.Machine.RestoreAsync(options, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidDataException>(failure);
        Assert.Equal(0, foreign.Installs);
        Assert.Null(runtime.State.Find(fixture.Actor));
        Assert.False(runtime.Recovery.Accepted.CaughtUp);
    }

    [Fact]
    public async Task VerifiedOlderPrefixMayCatchUpWhileAccountsAndWorkingProofRemainClosed()
    {
        // Arrange
        using ClusterTestDirectory directory = new();
        RevisionFixture fixture = new();
        ClusterOptions options = fixture.Options(directory.Location);
        RevisionSnapshotStore snapshots = new(Options.Create(options));
        PublishedRevisionSnapshot snapshot = await snapshots.PublishAsync(fixture.WithProjectionRows(), TestContext.Current.CancellationToken);
        RecoveryAdmission proof = new(options.ClusterId, 1, options.NodeId, options.StorageGeneration,
            true, false, snapshot.Manifest.SnapshotId, snapshot.Digest, snapshot.Image.Index + 1, 1);
        Projection projection = new(fixture.Baseline);
        using ServiceProvider scopes = new ServiceCollection().BuildServiceProvider();
        var runtime = Create(proof, projection, snapshots, scopes.GetRequiredService<IServiceScopeFactory>(), options);
        CancellationToken token = TestContext.Current.CancellationToken;
        await runtime.Machine.RestoreAsync(options, token);
        await using WriteAheadLog wal = new(WalOptions(Path.Combine(directory.Location, "wal")), runtime.Machine);
        await wal.InitializeAsync(token);

        // Act
        bool behind = await runtime.Recovery.ConfirmLocalReplayAsync(wal.LastCommittedEntryIndex, wal.LastAppliedIndex, 1, token);
        bool catchUp = await runtime.Recovery.VerifyCatchUpStartAsync(wal.LastCommittedEntryIndex, wal.LastAppliedIndex, 1, token);

        // Assert
        Assert.False(behind);
        Assert.True(catchUp);
        Assert.Null(runtime.State.Find(fixture.Actor));
        Assert.False(runtime.Recovery.Accepted.CaughtUp);

        // Act
        long index = await wal.AppendAsync(new BinaryLogEntry { Term = 2, CommandId = RevisionCodec.COMMAND_ID,
            Content = RevisionCodec.Encode(fixture.Server()) }, token);
        await wal.CommitAsync(index, token);
        await wal.WaitForApplyAsync(index, token);
        bool confirmed = await runtime.Recovery.ConfirmLocalReplayAsync(wal.LastCommittedEntryIndex, wal.LastAppliedIndex, 1, token);

        // Assert
        Assert.True(confirmed);
        Assert.NotNull(runtime.State.Find(fixture.Actor));
        Assert.False(runtime.Recovery.Accepted.Activated);
        Assert.False(runtime.Recovery.Accepted.ManuallyEnabled);
    }

    [Fact]
    public async Task AccountReplayAfterOlderSnapshotPreservesNewCredentialsAndDeletionBeforeReopeningAccess()
    {
        // Arrange
        using ClusterTestDirectory directory = new();
        RevisionFixture fixture = new();
        ClusterOptions options = fixture.Options(directory.Location);
        RevisionSnapshotStore snapshots = new(Options.Create(options));
        PublishedRevisionSnapshot snapshot = await snapshots.PublishAsync(fixture.WithProjectionRows(), TestContext.Current.CancellationToken);
        RecoveryAdmission proof = new(options.ClusterId, 1, options.NodeId, options.StorageGeneration,
            true, false, snapshot.Manifest.SnapshotId, snapshot.Digest, 5, 1);
        Projection projection = new(fixture.Baseline);
        using ServiceProvider scopes = new ServiceCollection().BuildServiceProvider();
        Guid subject = Guid.NewGuid();
        ConfigurationValue old = fixture.Baseline.Values[fixture.Actor].Value;
        RevisionCommand create = fixture.Server() with
        {
            EntityId = subject, EntityKind = DeletedEntityKind.Account,
            Value = old with { Name = "subject" }
        };
        RevisionCommand password = create with
        {
            OperationId = Guid.NewGuid(), EntityId = fixture.Actor, BaseRevision = 1,
            Value = old with { PasswordHash = "new-protected-hash", SecurityStamp = "new-stamp" }
        };
        RevisionCommand delete = create with
        {
            OperationId = Guid.NewGuid(), Action = "Delete", BaseRevision = 3, ActorRevision = 4, Value = null
        };
        string walPath = Path.Combine(directory.Location, "wal");

        // Act
        var first = Create(proof, projection, snapshots, scopes.GetRequiredService<IServiceScopeFactory>(), options);
        await first.Machine.RestoreAsync(options, TestContext.Current.CancellationToken);
        await using (WriteAheadLog wal = new(WalOptions(walPath), first.Machine))
        {
            await wal.InitializeAsync(TestContext.Current.CancellationToken);
            foreach (RevisionCommand command in new[] { create, password, delete })
            {
                long index = await wal.AppendAsync(new BinaryLogEntry
                {
                    Term = 2, CommandId = RevisionCodec.COMMAND_ID, Content = RevisionCodec.Encode(command)
                }, TestContext.Current.CancellationToken);
                await wal.CommitAsync(index, TestContext.Current.CancellationToken);
                await wal.WaitForApplyAsync(index, TestContext.Current.CancellationToken);
            }
        }

        var restarted = Create(proof, projection, snapshots, scopes.GetRequiredService<IServiceScopeFactory>(), options);
        AcceptedAccountSession sessions = new(restarted.State);
        var stale = sessions.Create(RevisionAccounts.Materialize(fixture.Baseline.Values[fixture.Actor]), "cookie");
        await restarted.Machine.RestoreAsync(options, TestContext.Current.CancellationToken);
        await using WriteAheadLog reopened = new(WalOptions(walPath), restarted.Machine);
        await reopened.InitializeAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(restarted.State.Find(fixture.Actor));
        Assert.False(sessions.IsValid(stale));

        // Act
        bool confirmed = await restarted.Recovery.ConfirmLocalReplayAsync(reopened.LastCommittedEntryIndex,
            reopened.LastAppliedIndex, 1, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(confirmed);
        RevisionImage accepted = restarted.State.Current!;
        Assert.Equal("new-protected-hash", accepted.Values[fixture.Actor].Value.PasswordHash);
        Assert.True(accepted.Values[subject].Deleted);
        Assert.False(restarted.State.Find(subject)!.IsEnabled);
        Assert.False(sessions.IsValid(stale));
        Assert.True(sessions.IsValid(sessions.Create(RevisionAccounts.Materialize(accepted.Values[fixture.Actor]), "cookie")));
        Assert.Equal(3, projection.Effects);
        Assert.False(restarted.Recovery.Accepted.Activated);
    }

    [Fact]
    public async Task RealWalReplaysProductionReducerAfterSnapshotBeforeConfirmingAccounts()
    {
        // Arrange
        using ClusterTestDirectory directory = new();
        RevisionFixture fixture = new();
        ClusterOptions options = fixture.Options(directory.Location);
        RevisionSnapshotStore snapshots = new(Options.Create(options));
        PublishedRevisionSnapshot snapshot = await snapshots.PublishAsync(fixture.WithProjectionRows(), TestContext.Current.CancellationToken);
        RecoveryAdmission proof = new(options.ClusterId, 1, options.NodeId, options.StorageGeneration,
            true, false, snapshot.Manifest.SnapshotId, snapshot.Digest, 2, 1);
        Projection projection = new(fixture.Baseline);
        using ServiceProvider scopes = new ServiceCollection().BuildServiceProvider();
        RevisionCommand command = fixture.Server();
        string walPath = Path.Combine(directory.Location, "wal");
        long index;

        // Act
        var first = Create(proof, projection, snapshots, scopes.GetRequiredService<IServiceScopeFactory>(), options);
        await first.Machine.RestoreAsync(options, TestContext.Current.CancellationToken);
        Assert.Null(first.State.Find(fixture.Actor));
        await using (WriteAheadLog wal = new(WalOptions(walPath), first.Machine))
        {
            await wal.InitializeAsync(TestContext.Current.CancellationToken);
            index = await wal.AppendAsync(new BinaryLogEntry
            {
                Term = 2, CommandId = RevisionCodec.COMMAND_ID, Content = RevisionCodec.Encode(command)
            }, TestContext.Current.CancellationToken);
            await wal.CommitAsync(index, TestContext.Current.CancellationToken);
            await wal.WaitForApplyAsync(index, TestContext.Current.CancellationToken);
        }

        var restarted = Create(proof, projection, snapshots, scopes.GetRequiredService<IServiceScopeFactory>(), options);
        await restarted.Machine.RestoreAsync(options, TestContext.Current.CancellationToken);
        await using WriteAheadLog reopened = new(WalOptions(walPath), restarted.Machine);
        await reopened.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Null(restarted.State.Find(fixture.Actor));
        bool confirmed = await restarted.Recovery.ConfirmLocalReplayAsync(reopened.LastCommittedEntryIndex,
            reopened.LastAppliedIndex, 1, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(confirmed);
        Assert.NotNull(restarted.State.Find(fixture.Actor));
        Assert.True(restarted.Recovery.Accepted.CaughtUp);
        Assert.False(restarted.Recovery.Accepted.Activated);
        Assert.False(restarted.Recovery.Accepted.ManuallyEnabled);
        Assert.Equal(index, restarted.State.Current!.Values[command.EntityId].Revision);
        Assert.Equal(1, projection.Effects);
        Assert.Equal(0, projection.Installs);
    }

    [Fact]
    public async Task FailedSnapshotCheckpointKeepsPreviouslyPublishedCut()
    {
        // Arrange
        using ClusterTestDirectory directory = new();
        RevisionFixture fixture = new();
        ClusterOptions options = fixture.Options(directory.Location);
        RevisionSnapshotStore snapshots = new(Options.Create(options));
        PublishedRevisionSnapshot snapshot = await snapshots.PublishAsync(fixture.WithProjectionRows(), TestContext.Current.CancellationToken);
        RecoveryAdmission proof = new(options.ClusterId, 1, options.NodeId, options.StorageGeneration,
            true, false, snapshot.Manifest.SnapshotId, snapshot.Digest, 2, 1);
        Projection projection = new(fixture.Baseline) { FailCheckpoint = true };
        using ServiceProvider scopes = new ServiceCollection().BuildServiceProvider();
        var runtime = Create(proof, projection, snapshots, scopes.GetRequiredService<IServiceScopeFactory>(), options);
        await runtime.Recovery.RestoreAsync(options.ClusterId, 1, options.NodeId, options.StorageGeneration, TestContext.Current.CancellationToken);
        await runtime.Recovery.ConfirmLocalReplayAsync(2, 2, 1, TestContext.Current.CancellationToken);
        PublishedRevisionSnapshot published = snapshots.Current!;

        // Act
        Exception? failure = await Record.ExceptionAsync(() => runtime.Recovery.CreateSnapshotAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<IOException>(failure);
        Assert.Same(published, snapshots.Current);
        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Location, "snapshots"), "*.tmp"));
    }

    [Fact]
    public async Task UnknownRecoveryAdmissionCannotOpenAccountsOrCreateStorage()
    {
        // Arrange
        using ClusterTestDirectory directory = new();
        RevisionFixture fixture = new();
        ClusterOptions options = fixture.Options(Path.Combine(directory.Location, "not-created"));
        using ServiceProvider scopes = new ServiceCollection().BuildServiceProvider();
        RevisionStateStore state = new();
        state.Publish(fixture.Baseline);
        Projection projection = new(fixture.Baseline);
        RevisionClusterRecovery recovery = new(new UnavailableRecoveryAdmission(), state,
            new RevisionApplyEngine(state, new RevisionReducer(), projection), projection,
            new RevisionSnapshotStore(Options.Create(options)), scopes.GetRequiredService<IServiceScopeFactory>(), Options.Create(options));

        // Act
        bool restored = await recovery.RestoreAsync(options.ClusterId, 1, options.NodeId,
            options.StorageGeneration, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(restored);
        Assert.False(recovery.Accepted.CaughtUp);
        Assert.Null(state.Find(fixture.Actor));
        Assert.False(Directory.Exists(options.StoragePath));
    }

    private static (RevisionStateStore State, RevisionClusterRecovery Recovery, ClusterStateMachine Machine) Create(
        RecoveryAdmission proof, Projection projection, RevisionSnapshotStore snapshots,
        IServiceScopeFactory scopes, ClusterOptions options)
    {
        RevisionStateStore state = new();
        RevisionClusterRecovery recovery = new(new Admission(proof), state,
            new RevisionApplyEngine(state, new RevisionReducer(), projection), projection,
            snapshots, scopes, Options.Create(options));
        return (state, recovery, new ClusterStateMachine(recovery, new NodeReadiness(TimeProvider.System)));
    }

    private static WriteAheadLog.Options WalOptions(string path)
    {
        return new() { Location = path, FlushInterval = TimeSpan.Zero, FlushOnCommit = true,
            HashAlgorithm = WriteAheadLog.IntegrityHashAlgorithm.Crc64 };
    }

    private class Admission(RecoveryAdmission proof) : IRecoveryAdmission
    {
        public ValueTask<RecoveryAdmission?> ReadAsync(CancellationToken token)
        {
            return ValueTask.FromResult<RecoveryAdmission?>(proof);
        }
    }

    internal class Projection(RevisionImage initial) : IRevisionProjectionStore
    {
        private ProjectionHead head = new(initial.ClusterId, initial.Generation, initial.Index, initial.Term, initial.Digest);
        private readonly Dictionary<long, string> receipts = [];
        public int Effects { get; private set; }
        public int Installs { get; private set; }
        public bool FailCheckpoint { get; init; }

        public Task ApplyAsync(ProjectionWriteSet changes, CancellationToken token)
        {
            if (receipts.TryGetValue(changes.Receipt.LogIndex, out string? digest))
            {
                Assert.Equal(digest, changes.Receipt.Digest);
                return Task.CompletedTask;
            }

            Assert.Equal(head.Index, changes.ExpectedCursorIndex);
            receipts.Add(changes.Receipt.LogIndex, changes.Receipt.Digest);
            head = new(head.ClusterId, head.Generation, changes.Receipt.LogIndex, changes.Receipt.Term, changes.Receipt.Digest);
            Effects++;
            return Task.CompletedTask;
        }

        public Task VerifyAsync(RevisionImage image, CancellationToken token)
        {
            Assert.Equal(image.Index, head.Index);
            Assert.Equal(image.Digest, head.Digest);
            return Task.CompletedTask;
        }

        public Task<ProjectionHead?> ReadHeadAsync(CancellationToken token)
        {
            return Task.FromResult<ProjectionHead?>(head);
        }

        public Task InstallAsync(RevisionImage image, Guid snapshotId, string digest, CancellationToken token)
        {
            Installs++;
            head = new(image.ClusterId, image.Generation, image.Index, image.Term, image.Digest);
            return Task.CompletedTask;
        }

        public Task<RevisionImage> ExportAsync(RevisionImage image, CancellationToken token)
        {
            return Task.FromResult(image);
        }

        public Task CheckpointAsync(Guid snapshotId, string digest, long index, CancellationToken token)
        {
            if (FailCheckpoint)
            {
                throw new IOException("CheckpointUnavailable");
            }

            return Task.CompletedTask;
        }
    }
}
