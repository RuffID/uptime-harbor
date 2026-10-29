namespace UptimeHarbor.Domain.Cluster;

public class RecoveryMarker : ProjectionRecord
{
    private RecoveryMarker()
    {
    }

    public RecoveryMarker(Guid id, JournalPosition position, Guid baselineSnapshotId, string baselineDigest,
        Guid previousClusterId, Guid actorUserId, bool lossAcknowledged, bool fencingConfirmed, bool maintenanceDisabled)
        : base(id, position)
    {
        StorageGuards.Id(baselineSnapshotId, nameof(baselineSnapshotId));
        StorageGuards.Id(previousClusterId, nameof(previousClusterId));
        StorageGuards.Id(actorUserId, nameof(actorUserId));
        BaselineSnapshotId = baselineSnapshotId;
        BaselineDigest = StorageGuards.Digest(baselineDigest);
        PreviousClusterId = previousClusterId;
        LossAcknowledged = lossAcknowledged;
        FencingConfirmed = fencingConfirmed;
        MaintenanceDisabled = maintenanceDisabled;
        ActorUserId = actorUserId;
    }

    public Guid BaselineSnapshotId { get; private set; }
    public string BaselineDigest { get; private set; } = string.Empty;
    public Guid PreviousClusterId { get; private set; }
    public bool LossAcknowledged { get; private set; }
    public bool FencingConfirmed { get; private set; }
    public bool MaintenanceDisabled { get; private set; }
    public Guid ActorUserId { get; private set; }
}
