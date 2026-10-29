using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Replication;
using UptimeHarbor.Application.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster;

public class DotNextConsensusAdapter(IRaftCluster cluster, IClusterStateView state,
    NodeReadiness readiness, ClusterOptions options) : IConsensusWriter
{
    public async ValueTask<long> ReplicateAsync(ReadOnlyMemory<byte> payload, int commandId,
        CancellationToken cancellationToken)
    {
        if (payload.IsEmpty || payload.Length > options.CommandMaxBytes || !readiness.CanWork(state))
        {
            throw new InvalidOperationException("Cluster writer is not ready.");
        }

        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, cluster.LeadershipToken, cluster.ConsensusToken);
        TaskCompletionSource<long> applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BinaryLogEntry entry = new()
        {
            Content = payload,
            Term = cluster.AuditTrail.Term,
            CommandId = commandId,
            Context = applied
        };
        IReplicationCluster<IRaftLogEntry> replication = cluster;
        await replication.ReplicateAsync(entry, cancellation.Token);
        long index = await applied.Task.WaitAsync(cancellation.Token);
        await cluster.AuditTrail.WaitForApplyAsync(index, cancellation.Token);
        return index;
    }

    public ValueTask<bool> ResignAsync(CancellationToken cancellationToken)
    {
        return cluster.ResignAsync(cancellationToken);
    }
}
