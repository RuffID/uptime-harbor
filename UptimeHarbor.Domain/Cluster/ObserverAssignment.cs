namespace UptimeHarbor.Domain.Cluster;

public class ObserverAssignment : ProjectionRecord
{
    private ObserverAssignment()
    {
    }

    public ObserverAssignment(Guid id, JournalPosition position, Guid targetId, Guid observerNodeId,
        Guid checkGeneration, long configRevision, DateTimeOffset assignedAtUtc) : base(id, position)
    {
        StorageGuards.Id(targetId, nameof(targetId));
        StorageGuards.Id(observerNodeId, nameof(observerNodeId));
        StorageGuards.Id(checkGeneration, nameof(checkGeneration));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configRevision);
        StorageGuards.Utc(assignedAtUtc);
        TargetId = targetId;
        ObserverNodeId = observerNodeId;
        CheckGeneration = checkGeneration;
        ConfigRevision = configRevision;
        AssignedAtUtc = assignedAtUtc;
    }

    public Guid TargetId { get; private set; }
    public Guid ObserverNodeId { get; private set; }
    public Guid CheckGeneration { get; private set; }
    public long ConfigRevision { get; private set; }
    public DateTimeOffset AssignedAtUtc { get; private set; }
}
