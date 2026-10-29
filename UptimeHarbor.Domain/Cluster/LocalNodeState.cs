namespace UptimeHarbor.Domain.Cluster;

// Локальный marker вне общего snapshot; голос, term и действующее право отправки здесь не хранятся.
public class LocalNodeState
{
    private LocalNodeState()
    {
    }

    public LocalNodeState(Guid nodeId, ClusterIdentity cluster, Guid storageGeneration,
        Guid sessionEpoch, string walStoreIdentity)
    {
        StorageGuards.Id(nodeId, nameof(nodeId));
        StorageGuards.Id(storageGeneration, nameof(storageGeneration));
        StorageGuards.Id(sessionEpoch, nameof(sessionEpoch));
        Id = nodeId;
        ClusterId = cluster.ClusterId;
        ClusterGeneration = cluster.Generation;
        StorageGeneration = storageGeneration;
        SessionEpoch = sessionEpoch;
        WalStoreIdentity = StorageGuards.Text(walStoreIdentity, 256, nameof(walStoreIdentity));
    }

    public Guid Id { get; private set; }
    public Guid ClusterId { get; private set; }
    public long ClusterGeneration { get; private set; }
    public Guid StorageGeneration { get; private set; }
    public Guid SessionEpoch { get; private set; }
    public string WalStoreIdentity { get; private set; } = string.Empty;
    public Guid? SnapshotId { get; private set; }
    public string? SnapshotDigest { get; private set; }
    public long SnapshotCutIndex { get; private set; }
    public long ReceivedIndex { get; private set; }
    public long CommittedIndex { get; private set; }
    public long MinReplayIndex { get; private set; }
    public bool IsQuarantined { get; private set; } = true;
    public bool IsMaintenanceDisabled { get; private set; } = true;
    public Guid? RecoveryId { get; private set; }

    public void RecordSnapshotCheckpoint(Guid snapshotId, string digest, long cutIndex, Guid newSessionEpoch)
    {
        StorageGuards.Id(snapshotId, nameof(snapshotId));
        StorageGuards.Id(newSessionEpoch, nameof(newSessionEpoch));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cutIndex);
        string canonicalDigest = StorageGuards.Digest(digest);
        if (cutIndex < SnapshotCutIndex || newSessionEpoch == SessionEpoch)
        {
            throw new InvalidOperationException("Snapshot checkpoint cannot restore stale sessions or state.");
        }

        SnapshotId = snapshotId;
        SnapshotDigest = canonicalDigest;
        SnapshotCutIndex = cutIndex;
        SessionEpoch = newSessionEpoch;
        IsQuarantined = true;
    }
}
