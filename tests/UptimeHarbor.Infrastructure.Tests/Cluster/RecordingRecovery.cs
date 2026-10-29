using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

internal class RecordingRecovery : IClusterRecovery
{
    public ClusterSnapshot? Snapshot => null;
    public AcceptedClusterState Accepted => AcceptedClusterState.Unknown;
    public List<byte[]> Applied { get; } = [];
    public bool FailApply { get; set; }

    public ValueTask<bool> RestoreAsync(Guid clusterId, long generation, Guid nodeId,
        Guid storageGeneration, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> ConfirmLocalReplayAsync(long committedIndex, long appliedIndex,
        long membershipVersion, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(true);
    }

    public async ValueTask<long> ApplyAsync(CommittedRecord entry, CancellationToken cancellationToken)
    {
        if (FailApply)
        {
            throw new InvalidOperationException("secret-payload-from-provider");
        }

        using MemoryStream stream = new();
        await entry.CopyPayloadAsync(stream, cancellationToken);
        Applied.Add(stream.ToArray());
        return entry.Index;
    }

    public ValueTask ReclaimAsync(long watermark, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }
}
