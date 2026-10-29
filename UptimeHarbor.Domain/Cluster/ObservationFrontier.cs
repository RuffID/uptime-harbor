namespace UptimeHarbor.Domain.Cluster;

public class ObservationFrontier : ProjectionRecord
{
    private ObservationFrontier()
    {
    }

    public ObservationFrontier(Guid id, JournalPosition position, Guid targetId, Guid observerNodeId,
        Guid storageGeneration, Guid checkGeneration, long acceptedSequence, DateTimeOffset checkedWatermarkUtc,
        Guid batchId, bool isHistoricalAssemblyOpen) : base(id, position)
    {
        StorageGuards.Id(targetId, nameof(targetId));
        StorageGuards.Id(observerNodeId, nameof(observerNodeId));
        StorageGuards.Id(storageGeneration, nameof(storageGeneration));
        StorageGuards.Id(checkGeneration, nameof(checkGeneration));
        StorageGuards.Id(batchId, nameof(batchId));
        ArgumentOutOfRangeException.ThrowIfNegative(acceptedSequence);
        StorageGuards.Utc(checkedWatermarkUtc);
        TargetId = targetId;
        ObserverNodeId = observerNodeId;
        StorageGeneration = storageGeneration;
        CheckGeneration = checkGeneration;
        AcceptedSequence = acceptedSequence;
        CheckedWatermarkUtc = checkedWatermarkUtc;
        BatchId = batchId;
        IsHistoricalAssemblyOpen = isHistoricalAssemblyOpen;
    }

    public Guid TargetId { get; private set; }
    public Guid ObserverNodeId { get; private set; }
    public Guid StorageGeneration { get; private set; }
    public Guid CheckGeneration { get; private set; }
    public long AcceptedSequence { get; private set; }
    public DateTimeOffset CheckedWatermarkUtc { get; private set; }
    public Guid BatchId { get; private set; }
    public bool IsHistoricalAssemblyOpen { get; private set; }
}
