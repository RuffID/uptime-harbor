namespace UptimeHarbor.Domain.Cluster;

public record ClusterIdentity
{
    public ClusterIdentity(Guid clusterId, long generation)
    {
        StorageGuards.Id(clusterId, nameof(clusterId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        ClusterId = clusterId;
        Generation = generation;
    }

    public Guid ClusterId { get; }
    public long Generation { get; }
}
