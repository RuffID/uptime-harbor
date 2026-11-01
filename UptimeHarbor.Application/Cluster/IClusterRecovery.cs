using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster;

/// <summary>Граница восстановления этапа 04. Подтверждение включает manifest, отсутствие клона и checkpoint SQL.</summary>
public interface IClusterRecovery
{
    ValueTask<bool> RestoreAsync(Guid clusterId, long generation, Guid nodeId,
        Guid storageGeneration, CancellationToken cancellationToken);
    ClusterSnapshot? Snapshot { get; }
    ValueTask<bool> ConfirmLocalReplayAsync(long committedIndex, long appliedIndex,
        long membershipVersion, CancellationToken cancellationToken);
    ValueTask<long> ApplyAsync(CommittedRecord entry, CancellationToken cancellationToken);
    ValueTask ReclaimAsync(long watermark, CancellationToken cancellationToken);
    AcceptedClusterState Accepted { get; }
}
