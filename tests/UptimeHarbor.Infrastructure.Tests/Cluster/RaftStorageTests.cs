using System.Text.Json;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.AspNetCore.Connections;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class RaftStorageTests
{
    [Fact]
    public async Task ApplyFailureIsLatchedAndDoesNotExposeProviderPayload()
    {
        // Arrange
        using ClusterTestDirectory directory = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        NodeReadiness readiness = new(TimeProvider.System);
        ClusterStateMachine firstMachine = new(new RecordingRecovery(), readiness);
        await firstMachine.RestoreAsync(new(), timeout.Token);
        await using (WriteAheadLog first = new(WalOptions(directory.Location), firstMachine))
        {
            await first.InitializeAsync(timeout.Token);
            long index = await first.AppendAsync(new BinaryLogEntry { Term = 1, CommandId = 1, Content = new byte[] { 7 } }, timeout.Token);
            await first.CommitAsync(index, timeout.Token);
            await first.WaitForApplyAsync(index, timeout.Token);
        }

        RecordingRecovery recovery = new() { FailApply = true };
        ClusterStateMachine machine = new(recovery, readiness);
        await machine.RestoreAsync(new(), timeout.Token);
        await using WriteAheadLog replay = new(WalOptions(directory.Location), machine);

        // Act
        Exception? error = await Record.ExceptionAsync(async () => await replay.InitializeAsync(timeout.Token));
        readiness.Report("Database", true, "Ready", TimeSpan.FromSeconds(10));

        // Assert
        Assert.NotNull(error);
        Assert.DoesNotContain("secret-payload-from-provider", error.ToString());
        Assert.True(machine.Failed);
        Assert.Equal("Ready", readiness.Get("Database").Status);
        Assert.Equal("Unavailable", readiness.Get("Projection").Status);
        Assert.Equal("ProjectionUnavailable", readiness.Get("Projection").Reason);
        Assert.NotNull(readiness.Get("Projection").Age);
    }

    [Fact]
    public void UnrestoredStateMachinePreventsWalConstruction()
    {
        // Arrange
        using ClusterTestDirectory directory = new();
        ClusterStateMachine machine = new(new UnavailableClusterRecovery(), new NodeReadiness(TimeProvider.System));

        // Act
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            new WriteAheadLog(WalOptions(directory.Location), machine));

        // Assert
        Assert.Contains("Restore", exception.Message);
        Assert.Empty(Directory.GetFiles(directory.Location));
    }

    [Fact]
    public async Task RealDotNextWalRestoresTermVoteAndCommittedPayloadAfterReopen()
    {
        // Arrange
        using ClusterTestDirectory directory = new();
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ClusterMemberId candidate = ClusterMemberId.FromEndPoint(new UriEndPoint(new Uri("https://a.example/internal/raft")));
        ClusterMemberId other = ClusterMemberId.FromEndPoint(new UriEndPoint(new Uri("https://b.example/internal/raft")));
        RecordingRecovery firstRecovery = new();
        ClusterStateMachine firstMachine = new(firstRecovery, new NodeReadiness(TimeProvider.System));
        await firstMachine.RestoreAsync(new(), cancellation);
        long index;

        // Act
        await using (WriteAheadLog first = new(WalOptions(directory.Location), firstMachine))
        {
            await first.InitializeAsync(cancellation);
            IPersistentState state = first;
            await state.IncrementTermAsync(candidate, cancellation);
            index = await first.AppendAsync(new BinaryLogEntry { Term = 1, Content = new byte[] { 7, 8, 9 }, CommandId = 1 }, cancellation);
            // Только локальный storage commit; это не имитация majority acceptance.
            await first.CommitAsync(index, cancellation);
            await state.WaitForApplyAsync(index, cancellation);
        }

        RecordingRecovery secondRecovery = new();
        ClusterStateMachine secondMachine = new(secondRecovery, new NodeReadiness(TimeProvider.System));
        await secondMachine.RestoreAsync(new(), cancellation);
        await using WriteAheadLog second = new(WalOptions(directory.Location), secondMachine);
        await second.InitializeAsync(cancellation);
        IPersistentState reopened = second;

        // Assert
        Assert.Equal(1, reopened.Term);
        Assert.True(reopened.IsVotedFor(candidate));
        Assert.False(reopened.IsVotedFor(other));
        Assert.Equal(index, second.LastCommittedEntryIndex);
        Assert.Contains(secondRecovery.Applied, payload => payload.SequenceEqual(new byte[] { 7, 8, 9 }));
    }

    [Fact]
    public void SingletonProvisioningCannotBeRepeatedOrReplaceMissingPreviousState()
    {
        // Arrange
        using ClusterTestDirectory directory = new();
        ClusterOptions options = Provision(directory.Location);

        // Act
        using (ClusterStorageLease first = new(options))
        {
            Assert.True(first.ColdStart);
            Assert.Throws<IOException>(() => new ClusterStorageLease(options));
        }

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => new ClusterStorageLease(options));

        // Assert
        Assert.Contains("incomplete", error.Message);
    }

    [Fact]
    public void TruncatedVoteStateIsRejectedBeforeDotNextCanClearIt()
    {
        // Arrange
        using ClusterTestDirectory directory = new();
        ClusterOptions options = Provision(directory.Location);
        using (ClusterStorageLease first = new(options))
        {
            Assert.True(first.ColdStart);
        }

        Directory.CreateDirectory(Path.Combine(directory.Location, "wal"));
        File.WriteAllBytes(Path.Combine(directory.Location, "wal", "state"), [0]);
        File.WriteAllBytes(Path.Combine(directory.Location, "membership"), new byte[8]);

        // Act
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => new ClusterStorageLease(options));

        // Assert
        Assert.Contains("incomplete", error.Message);
        Assert.Single(File.ReadAllBytes(Path.Combine(directory.Location, "wal", "state")));
    }

    private static WriteAheadLog.Options WalOptions(string path)
    {
        return new() { Location = path, FlushInterval = TimeSpan.Zero, FlushOnCommit = true,
            HashAlgorithm = WriteAheadLog.IntegrityHashAlgorithm.Crc64 };
    }

    private static ClusterOptions Provision(string path)
    {
        ClusterOptions options = new()
        {
            Mode = "Bootstrap", StoragePath = path, ClusterId = Guid.NewGuid(), Generation = 1,
            NodeId = Guid.NewGuid(), StorageGeneration = Guid.NewGuid(), PublicUri = "https://a.example/internal/raft"
        };
        ClusterDiskManifest manifest = new(options.ClusterId, 1, options.NodeId, options.StorageGeneration,
            options.PublicUri, Guid.NewGuid());
        File.WriteAllBytes(Path.Combine(path, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest));
        return options;
    }
}
