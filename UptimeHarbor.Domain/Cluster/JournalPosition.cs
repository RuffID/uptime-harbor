namespace UptimeHarbor.Domain.Cluster;

public record JournalPosition
{
    public JournalPosition(ClusterIdentity cluster, long logIndex, long term)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(logIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(term);
        Cluster = cluster;
        LogIndex = logIndex;
        Term = term;
    }

    public ClusterIdentity Cluster { get; }
    public long LogIndex { get; }
    public long Term { get; }

    public void EnsureSameCluster(ClusterIdentity other)
    {
        if (Cluster != other)
        {
            throw new InvalidOperationException("Journal positions belong to different cluster generations.");
        }
    }
}
