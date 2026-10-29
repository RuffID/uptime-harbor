namespace UptimeHarbor.Domain.Cluster;

public class ObserverResult : ProjectionRecord
{
    private ObserverResult()
    {
    }

    public ObserverResult(Guid observationId, JournalPosition position, Guid targetId, Guid observerNodeId,
        Guid storageGeneration, Guid checkGeneration, long configRevision, long sequence,
        DateTimeOffset checkedAtUtc, long durationMilliseconds, bool succeeded, string? failureKind,
        string? endpointName, DateTimeOffset? startedAtUtc, int consecutiveFailures, bool historicalOnly,
        string digest) : base(observationId, position)
    {
        StorageGuards.Id(targetId, nameof(targetId));
        StorageGuards.Id(observerNodeId, nameof(observerNodeId));
        StorageGuards.Id(storageGeneration, nameof(storageGeneration));
        StorageGuards.Id(checkGeneration, nameof(checkGeneration));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configRevision);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        ArgumentOutOfRangeException.ThrowIfNegative(durationMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(consecutiveFailures);
        StorageGuards.Utc(checkedAtUtc);
        if (startedAtUtc.HasValue)
        {
            StorageGuards.Utc(startedAtUtc.Value);
        }

        if (startedAtUtc > checkedAtUtc || succeeded && consecutiveFailures != 0)
        {
            throw new ArgumentException("Invalid observer result.");
        }

        if (endpointName is not null)
        {
            StorageGuards.Text(endpointName, ModelConstraints.OBJECT_NAME_MAX_LENGTH, nameof(endpointName));
        }

        if (failureKind is not null)
        {
            StorageGuards.Text(failureKind, 64, nameof(failureKind));
        }

        TargetId = targetId;
        ObserverNodeId = observerNodeId;
        StorageGeneration = storageGeneration;
        CheckGeneration = checkGeneration;
        ConfigRevision = configRevision;
        Sequence = sequence;
        CheckedAtUtc = checkedAtUtc;
        DurationMilliseconds = durationMilliseconds;
        Succeeded = succeeded;
        FailureKind = failureKind;
        EndpointName = endpointName;
        StartedAtUtc = startedAtUtc;
        ConsecutiveFailures = consecutiveFailures;
        HistoricalOnly = historicalOnly;
        Digest = StorageGuards.Digest(digest);
    }

    public Guid TargetId { get; private set; }
    public Guid ObserverNodeId { get; private set; }
    public Guid StorageGeneration { get; private set; }
    public Guid CheckGeneration { get; private set; }
    public long ConfigRevision { get; private set; }
    public long Sequence { get; private set; }
    public DateTimeOffset CheckedAtUtc { get; private set; }
    public long DurationMilliseconds { get; private set; }
    public bool Succeeded { get; private set; }
    public string? FailureKind { get; private set; }
    public string? EndpointName { get; private set; }
    public DateTimeOffset? StartedAtUtc { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public bool HistoricalOnly { get; private set; }
    public string Digest { get; private set; } = string.Empty;
}
