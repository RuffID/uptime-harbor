namespace UptimeHarbor.Domain.Cluster;

// Только квитанция применения SQL; payload для воспроизведения команд здесь не хранится.
public class AppliedJournalReceipt : ProjectionRecord
{
    private AppliedJournalReceipt()
    {
    }

    public AppliedJournalReceipt(Guid entryId, JournalPosition position, string digest)
        : base(entryId, position)
    {
        Digest = StorageGuards.Digest(digest);
    }

    public string Digest { get; private set; } = string.Empty;
}
