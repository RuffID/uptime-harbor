using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster;

public class UnavailableClusterRecovery : IClusterRecovery
{
    public ClusterSnapshot? Snapshot => null;
    public AcceptedClusterState Accepted => AcceptedClusterState.Unknown;

    public ValueTask<bool> RestoreAsync(Guid clusterId, long generation, Guid nodeId,
        Guid storageGeneration, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(false);
    }

    public ValueTask<long> ApplyAsync(CommittedRecord entry, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Cluster projection recovery is unavailable.");
    }

    public ValueTask<bool> ConfirmLocalReplayAsync(long committedIndex, long appliedIndex,
        long membershipVersion, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(false);
    }

    public ValueTask ReclaimAsync(long watermark, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Cluster snapshot recovery is unavailable.");
    }
}
