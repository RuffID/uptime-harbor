namespace UptimeHarbor.Domain.Cluster;

public class IncidentEvent : ProjectionRecord
{
    private IncidentEvent()
    {
    }

    public IncidentEvent(Guid id, JournalPosition position, Guid incidentId, long sequence, string kind,
        DateTimeOffset occurredAtUtc, string frontierDigest, string protectedPayload) : base(id, position)
    {
        StorageGuards.Id(incidentId, nameof(incidentId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        StorageGuards.Utc(occurredAtUtc);
        IncidentId = incidentId;
        Sequence = sequence;
        Kind = StorageGuards.Text(kind, 64, nameof(kind));
        OccurredAtUtc = occurredAtUtc;
        FrontierDigest = StorageGuards.Digest(frontierDigest);
        ProtectedPayload = StorageGuards.Payload(protectedPayload);
    }

    public Guid IncidentId { get; private set; }
    public long Sequence { get; private set; }
    public string Kind { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; private set; }
    public string FrontierDigest { get; private set; } = string.Empty;
    public string ProtectedPayload { get; private set; } = string.Empty;
}
