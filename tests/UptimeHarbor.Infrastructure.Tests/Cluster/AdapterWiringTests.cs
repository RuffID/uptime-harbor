using System.Reflection;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class AdapterWiringTests
{
    [Fact]
    public async Task ExactBootstrapUsesNativeWriterDespiteClosedWorkGateAndPayloadlessAppliedIndex()
    {
        // Arrange
        using ClusterTestDirectory directory = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        RevisionFixture fixture = new();
        ClusterOptions options = fixture.Options(directory.Location);
        options.Mode = "Bootstrap";
        RevisionCommand command = fixture.Server() with
        {
            EntityKind = UptimeHarbor.Domain.Cluster.DeletedEntityKind.Account, EntityId = fixture.Actor,
            ActorRevision = 1, Action = "BootstrapAccount", Value = fixture.Baseline.Values[fixture.Actor].Value
        };
        RevisionStateStore accepted = new();
        accepted.Publish(new RevisionImage(options.ClusterId, 1) { BootstrapAccountDigest = RevisionCodec.Digest(command) });
        NodeReadiness readiness = new(TimeProvider.System);
        ClusterStateMachine machine = new(new RecordingRecovery(), readiness);
        await machine.RestoreAsync(options, token);
        await using WriteAheadLog wal = new(new WriteAheadLog.Options
        {
            Location = directory.Location, FlushInterval = TimeSpan.Zero, FlushOnCommit = true,
            HashAlgorithm = WriteAheadLog.IntegrityHashAlgorithm.Crc64
        }, machine);
        await wal.InitializeAsync(token);
        IRaftCluster cluster = DispatchProxy.Create<IRaftCluster, RaftClusterProbe>();
        RaftClusterProbe probe = (RaftClusterProbe)cluster;
        probe.Store = wal;
        probe.Replicate = async (entry, cancellation) =>
        {
            long index = await wal.AppendAsync(entry, cancellation);
            await wal.CommitAsync(index, cancellation);
        };
        BootstrapView view = new(options);
        foreach (string component in new[] { "Database", "Storage", "Clock", "Projection" })
        {
            readiness.Report(component, true, "Ready", TimeSpan.FromMinutes(1));
        }

        DotNextConsensusAdapter adapter = new(cluster, view, readiness, options, accepted);

        // Act
        bool workOpen = readiness.CanWork(view);
        long applied = await adapter.ReplicateAsync(RevisionCodec.Encode(command), RevisionCodec.COMMAND_ID, token);
        Exception? otherCommand = await Record.ExceptionAsync(async () => await adapter.ReplicateAsync(
            RevisionCodec.Encode(command with { OperationId = Guid.NewGuid() }), RevisionCodec.COMMAND_ID, token));
        readiness.Report("Projection", false, "ProjectionUnavailable", TimeSpan.FromMinutes(1));
        Exception? applyFault = await Record.ExceptionAsync(async () => await adapter.ReplicateAsync(
            RevisionCodec.Encode(command), RevisionCodec.COMMAND_ID, token));

        // Assert
        Assert.False(workOpen);
        Assert.Equal(wal.LastAppliedIndex, applied);
        Assert.Equal(1, probe.Calls);
        Assert.IsType<InvalidOperationException>(otherCommand);
        Assert.IsType<InvalidOperationException>(applyFault);
    }

    [Fact]
    public async Task AdapterUsesDotNextReplicationAndWaitsForAppliedWalContext()
    {
        // Arrange
        using ClusterTestDirectory directory = new();
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        RecordingRecovery recovery = new();
        NodeReadiness readiness = new(TimeProvider.System);
        ClusterStateMachine machine = new(recovery, readiness);
        await machine.RestoreAsync(new(), cancellation);
        await using WriteAheadLog wal = new(new WriteAheadLog.Options
        {
            Location = directory.Location, FlushInterval = TimeSpan.Zero, FlushOnCommit = true,
            HashAlgorithm = WriteAheadLog.IntegrityHashAlgorithm.Crc64
        }, machine);
        await wal.InitializeAsync(cancellation);
        IRaftCluster cluster = DispatchProxy.Create<IRaftCluster, RaftClusterProbe>();
        RaftClusterProbe probe = (RaftClusterProbe)cluster;
        probe.Store = wal;
        probe.Replicate = async (entry, token) =>
        {
            // Подмена только границы replication API. Это локальный storage commit, не выборы/majority.
            long index = await wal.AppendAsync(entry, token);
            await wal.CommitAsync(index, token);
        };
        View view = new();
        foreach (string component in new[] { "Database", "Storage", "Clock" })
        {
            readiness.Report(component, true, "Ready", TimeSpan.FromMinutes(1));
        }

        readiness.ConfirmRecovery(readiness.RecoveryVersion, view.Accepted, view.Consensus);
        DotNextConsensusAdapter adapter = new(cluster, view, readiness, new ClusterOptions());

        // Act
        long applied = await adapter.ReplicateAsync(new byte[] { 42, 8 }, 1, cancellation);
        readiness.Report("Database", false, "DatabaseUnavailable", TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await adapter.ReplicateAsync(new byte[] { 99 }, 1, cancellation));

        // Assert
        Assert.Equal(1, probe.Calls);
        Assert.Equal(wal.LastAppliedIndex, applied);
        Assert.Equal(wal.LastCommittedEntryIndex, applied);
        Assert.Contains(recovery.Applied, bytes => bytes.SequenceEqual(new byte[] { 42, 8 }));
    }

    private class View : IClusterStateView
    {
        public ConsensusStatus Consensus { get; } = new(Guid.NewGuid(), 1, Guid.NewGuid(), 0, 1,
            true, 0, new HashSet<Guid>(), CancellationToken.None, CancellationToken.None);
        public AcceptedClusterState Accepted { get; } = new(true, true, true, true, true, 0, 1, Guid.Empty, false);
    }

    private class BootstrapView(ClusterOptions options) : IClusterStateView
    {
        public ConsensusStatus Consensus { get; } = new(options.ClusterId, 1, options.NodeId, 1, 1,
            true, 1, new HashSet<Guid> { options.NodeId }, CancellationToken.None, CancellationToken.None);
        public AcceptedClusterState Accepted { get; } = new(false, true, true, false, false, 1, 1, Guid.Empty, false);
    }
}
