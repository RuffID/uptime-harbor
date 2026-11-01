namespace UptimeHarbor.Domain.Cluster;

// Локальный поток: резервирование sequence и сохранение результата выполняются одной транзакцией.
public class ObservationCaptureCursor
{
    private ObservationCaptureCursor()
    {
    }

    public ObservationCaptureCursor(Guid streamId, Guid targetId, Guid observerNodeId,
        Guid storageGeneration, Guid checkGeneration)
    {
        StorageGuards.Id(streamId, nameof(streamId));
        StorageGuards.Id(targetId, nameof(targetId));
        StorageGuards.Id(observerNodeId, nameof(observerNodeId));
        StorageGuards.Id(storageGeneration, nameof(storageGeneration));
        StorageGuards.Id(checkGeneration, nameof(checkGeneration));
        Id = streamId;
        TargetId = targetId;
        ObserverNodeId = observerNodeId;
        StorageGeneration = storageGeneration;
        CheckGeneration = checkGeneration;
    }

    public Guid Id { get; private set; }
    public Guid TargetId { get; private set; }
    public Guid ObserverNodeId { get; private set; }
    public Guid StorageGeneration { get; private set; }
    public Guid CheckGeneration { get; private set; }
    public long LastSequence { get; private set; }

    public CapturedObservation Capture(Guid observationId, string digest, string payload, DateTimeOffset checkedAtUtc)
    {
        long sequence = checked(LastSequence + 1);
        CapturedObservation observation = new(observationId, Id, sequence, digest, payload, checkedAtUtc);
        LastSequence = sequence;
        return observation;
    }
}
