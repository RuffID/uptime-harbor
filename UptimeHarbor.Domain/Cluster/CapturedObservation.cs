namespace UptimeHarbor.Domain.Cluster;

public class CapturedObservation
{
    private CapturedObservation()
    {
    }

    public CapturedObservation(Guid id, Guid streamId, long sequence, string digest, string payload,
        DateTimeOffset checkedAtUtc)
    {
        StorageGuards.Id(id, nameof(id));
        StorageGuards.Id(streamId, nameof(streamId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        StorageGuards.Utc(checkedAtUtc);
        Id = id;
        StreamId = streamId;
        Sequence = sequence;
        Digest = StorageGuards.Digest(digest);
        Payload = StorageGuards.Payload(payload);
        CheckedAtUtc = checkedAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid StreamId { get; private set; }
    public long Sequence { get; private set; }
    public string Digest { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public DateTimeOffset CheckedAtUtc { get; private set; }
    public long? AcceptedSequence { get; private set; }
}
