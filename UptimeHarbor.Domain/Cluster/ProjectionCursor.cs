namespace UptimeHarbor.Domain.Cluster;

public class ProjectionCursor
{
    private ProjectionCursor()
    {
    }

    public ProjectionCursor(Guid nodeId, ClusterIdentity cluster)
    {
        StorageGuards.Id(nodeId, nameof(nodeId));
        Id = nodeId;
        ClusterId = cluster.ClusterId;
        ClusterGeneration = cluster.Generation;
    }

    public Guid Id { get; private set; }
    public Guid ClusterId { get; private set; }
    public long ClusterGeneration { get; private set; }
    public long LogIndex { get; private set; }
    public long Term { get; private set; }
    public string? Digest { get; private set; }
    public Guid? InstalledSnapshotId { get; private set; }

    public void Advance(long expectedIndex, JournalPosition position, string digest)
    {
        position.EnsureSameCluster(new ClusterIdentity(ClusterId, ClusterGeneration));
        string canonicalDigest = StorageGuards.Digest(digest);
        if (LogIndex != expectedIndex || position.LogIndex <= LogIndex || position.Term < Term)
        {
            throw new InvalidOperationException("Projection cursor does not match the proven prefix.");
        }

        LogIndex = position.LogIndex;
        Term = position.Term;
        Digest = canonicalDigest;
    }

    public void RecordSnapshot(Guid snapshotId)
    {
        StorageGuards.Id(snapshotId, nameof(snapshotId));
        InstalledSnapshotId = snapshotId;
    }
}
