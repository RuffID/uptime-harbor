using System.Buffers.Binary;
using DotNext.Net.Cluster.Consensus.Raft;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster.Models;
using UptimeHarbor.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class ManagedStorageSealTests
{
    [Theory]
    [InlineData("state", 0)]
    [InlineData("state", 1)]
    [InlineData("state", -1)]
    [InlineData("checkpoint", 0)]
    [InlineData("checkpoint", 1)]
    [InlineData("checkpoint", -1)]
    public async Task TruncatedWalCannotRestoreManagedConsensus(string file, int length)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        await first.StopStorageAsync();
        string walState = Path.Combine(first.Options.StoragePath, "wal", file);
        using (FileStream damaged = new(walState, FileMode.Open, FileAccess.Write))
        {
            long damagedLength = length;
            if (length < 0)
            {
                damagedLength = damaged.Length - 1;
            }

            damaged.SetLength(damagedLength);
        }

        await using ManagedRestartFixture restart = new(first);

        // Act
        Exception? failure = await Record.ExceptionAsync(() => restart.RestoreAsync(token));

        // Assert
        Assert.IsType<InvalidDataException>(failure);
        Assert.Null(restart.Native.Store);
        Assert.Null(restart.Admission.Read());
        Assert.Null(restart.Tls.Certificate);
        Assert.False(restart.Runtime.Accepted.CaughtUp);
        Assert.False(restart.Installation.CommonOpen);
        Assert.False(restart.Enable.Allows());
    }

    [Theory]
    [InlineData("term")]
    [InlineData("committed")]
    [InlineData("last")]
    [InlineData("version")]
    [InlineData("missing-checkpoint")]
    public async Task StaleOrUnknownWalIsRejectedBeforeNativeOpen(string damage)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        await first.StopStorageAsync();
        string path = Path.Combine(first.Options.StoragePath, "wal", "checkpoint");
        byte[] bytes = File.ReadAllBytes(path);
        switch (damage)
        {
            case "term":
                path = Path.Combine(first.Options.StoragePath, "wal", "state");
                bytes = File.ReadAllBytes(path);
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(bytes.Length - sizeof(long)), 0);
                break;
            case "committed":
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(4), 0);
                break;
            case "last":
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(12), 0);
                break;
            case "version":
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, uint.MaxValue);
                break;
            case "missing-checkpoint":
                File.Delete(path);
                break;
        }

        if (damage != "missing-checkpoint")
        {
            File.WriteAllBytes(path, bytes);
        }

        await using ManagedRestartFixture restart = new(first);

        // Act
        Exception? failure = await Record.ExceptionAsync(() => restart.RestoreAsync(token));

        // Assert
        Assert.IsType<InvalidDataException>(failure);
        Assert.Null(restart.Native.Store);
        Assert.Null(restart.Admission.Read());
        Assert.False(restart.Runtime.Accepted.CaughtUp);
        Assert.False(restart.Installation.CommonOpen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointFailureKeepsPreviousSealAndClosesRecovery(bool afterSql)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        PublishedRevisionSnapshot previous = first.Snapshots.Current!;
        string path = Path.Combine(first.Local.StateDirectory, "managed-storage.seal");
        byte[] seal = File.ReadAllBytes(path);
        int checkpoints = first.Projection.Checkpoints;
        if (afterSql)
        {
            first.Projection.AfterCheckpoint = cancellation =>
            {
                File.WriteAllText(path + ".next", "unknown");
                return Task.CompletedTask;
            };
        }
        else
        {
            first.Projection.BeforeCheckpoint = cancellation => throw new IOException("TestCheckpointFailure");
        }

        // Act
        Exception? failure = await Record.ExceptionAsync(() => first.Recovery.CreateSnapshotAsync(token));

        // Assert
        Assert.NotNull(failure);
        Assert.Same(previous, first.Snapshots.Current);
        Assert.Equal(seal, File.ReadAllBytes(path));
        Assert.False(first.Recovery.Accepted.CaughtUp);
        Assert.False(first.Enable.Allows());
        Assert.Equal(checkpoints + Convert.ToInt32(afterSql), first.Projection.Checkpoints);

        // Arrange
        await first.StopStorageAsync();
        await using ManagedRestartFixture restart = new(first);

        // Act
        Exception? restartFailure = await Record.ExceptionAsync(() => restart.RestoreAsync(token));

        // Assert
        if (afterSql)
        {
            Assert.IsType<InvalidDataException>(restartFailure);
            Assert.Null(restart.Admission.Read());
        }
        else
        {
            Assert.Null(restartFailure);
            Assert.True(restart.Recovery.Accepted.CaughtUp);
        }

        Assert.False(restart.Installation.CommonOpen);
    }

    [Fact]
    public async Task SealBeforeCurrentPublicationRestoresExactSnapshot()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        PublishedRevisionSnapshot previous = first.Snapshots.Current!;
        PublishedRevisionSnapshot next = await first.Snapshots.PublishAsync(first.Accepted.Current!, token);
        await first.Projection.CheckpointAsync(next.Manifest.SnapshotId, next.Digest, next.Image.Index, token);

        // Act
        await first.Seals!.WriteAsync(next, token);

        // Assert
        Assert.Same(previous, first.Snapshots.Current);
        Assert.Equal(next.Manifest.SnapshotId, first.Seals.Read(first.Installation.Current!).SnapshotId);

        // Arrange
        await first.StopStorageAsync();
        await using ManagedRestartFixture restart = new(first);

        // Act
        await restart.RestoreAsync(token);

        // Assert
        Assert.Equal(next.Manifest.SnapshotId, restart.Admission.Read()!.SnapshotId);
        Assert.True(restart.Recovery.Accepted.CaughtUp);
        Assert.False(restart.Installation.CommonOpen);
    }

    [Fact]
    public async Task ConcurrentSnapshotAndDurableApplyPublishMonotonicSeals()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        long originalIndex = first.Accepted.Current!.Index;
        int exports = first.Projection.Exports;
        int checkpoints = first.Projection.Checkpoints;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Accepted.Changed += () =>
        {
            if (first.Accepted.Current!.Index > originalIndex)
            {
                applied.TrySetResult();
            }
        };

        first.Projection.BeforeCheckpoint = async cancellation =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellation);
        };

        // Act
        Task snapshot = first.Recovery.CreateSnapshotAsync(token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        Task apply = first.Native.Replicate(new BinaryLogEntry
        {
            Term = 1, CommandId = RevisionCodec.COMMAND_ID, Content = RevisionCodec.Encode(first.Account)
        }, token).AsTask();
        try
        {
            await applied.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.False(snapshot.IsCompleted);
            Assert.False(apply.IsCompleted);
            Assert.Equal(originalIndex, first.Snapshots.Current!.Image.Index);
        }
        finally
        {
            release.TrySetResult();
        }

        await Task.WhenAll(snapshot, apply).WaitAsync(TimeSpan.FromSeconds(10), token);

        // Assert
        ManagedStorageSeal seal = first.Seals!.Read(first.Installation.Current!);
        Assert.True(seal.Index > originalIndex);
        Assert.Equal(first.Accepted.Current!.Index, seal.Index);
        Assert.Equal(first.Projection.CheckpointId, seal.SnapshotId);
        Assert.Equal(first.Snapshots.Current!.Manifest.SnapshotId, seal.SnapshotId);
        Assert.Equal(exports + 2, first.Projection.Exports);
        Assert.Equal(checkpoints + 2, first.Projection.Checkpoints);

        // Act
        await first.StopStorageAsync();
        await using ManagedRestartFixture restart = new(first);
        await restart.RestoreAsync(token);

        // Assert
        Assert.Equal(seal.Index, restart.Accepted.Current!.Index);
        Assert.True(restart.Recovery.Accepted.CaughtUp);
        Assert.False(restart.Installation.CommonOpen);
    }

    [Theory]
    [InlineData("index")]
    [InlineData("term")]
    [InlineData("same-index-term")]
    [InlineData("same-index-head")]
    public async Task SealRejectsRollbackOrConflictingCuts(string change)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        await first.Native.Store.UpdateTermAsync(2, true, token);
        await first.Native.Replicate(new BinaryLogEntry
        {
            Term = 2, CommandId = RevisionCodec.COMMAND_ID, Content = RevisionCodec.Encode(first.Account)
        }, token);
        PublishedRevisionSnapshot current = first.Snapshots.Current!;
        RevisionImage changed = current.Image;
        switch (change)
        {
            case "index":
                changed = changed with { Index = changed.Index - 1 };
                break;
            case "term":
                changed = changed with { Index = changed.Index + 1, Term = changed.Term - 1 };
                break;
            case "same-index-term":
                changed = changed with { Term = changed.Term + 1 };
                break;
            case "same-index-head":
                changed = changed with { Digest = new string('A', 64) };
                break;
        }

        PublishedRevisionSnapshot candidate = await first.Snapshots.PublishAsync(changed, token);
        string path = Path.Combine(first.Local.StateDirectory, "managed-storage.seal");
        byte[] original = File.ReadAllBytes(path);

        // Act
        Exception? failure = await Record.ExceptionAsync(() => first.Seals!.WriteAsync(candidate, token));

        // Assert
        Assert.IsType<InvalidDataException>(failure);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Same(current, first.Snapshots.Current);
    }

    [Fact]
    public async Task OlderWalCheckpointCannotBeHiddenByNewerSealedSnapshot()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        string checkpoint = Path.Combine(first.Options.StoragePath, "wal", "checkpoint");
        byte[] old;
        using (FileStream stream = new(checkpoint, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            old = new byte[stream.Length];
            stream.ReadExactly(old);
        }

        await first.Native.Replicate(new BinaryLogEntry
        {
            Term = 1, CommandId = RevisionCodec.COMMAND_ID, Content = RevisionCodec.Encode(first.Account)
        }, token);
        await first.StopStorageAsync();
        File.WriteAllBytes(checkpoint, old);
        await using ManagedRestartFixture restart = new(first);

        // Act
        Exception? failure = await Record.ExceptionAsync(() => restart.RestoreAsync(token));

        // Assert
        Assert.IsType<InvalidDataException>(failure);
        Assert.Null(restart.Admission.Read());
        Assert.False(restart.Recovery.Accepted.CaughtUp);
        Assert.Equal(old, File.ReadAllBytes(checkpoint));
    }

    [Fact]
    public async Task LaterElectionTermWithoutApplyStillAllowsQuarantinedRestart()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        long cut = first.Seals!.Read(first.Installation.Current!).Index;
        await first.Native.Store.UpdateTermAsync(3, true, token);
        await first.StopStorageAsync();
        await using ManagedRestartFixture restart = new(first);

        // Act
        await restart.RestoreAsync(token);

        // Assert
        Assert.Equal(3, restart.Native.Store.Term);
        Assert.Equal(cut, restart.Accepted.Current!.Index);
        Assert.True(restart.Recovery.Accepted.CaughtUp);
        Assert.False(restart.Installation.CommonOpen);
        Assert.False(restart.Enable.Allows());
    }
}
