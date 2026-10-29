namespace UptimeHarbor.Domain.Cluster;

public class Tombstone : ProjectionRecord
{
    private Tombstone()
    {
    }

    public Tombstone(Guid entityId, JournalPosition position, DeletedEntityKind kind, DateTimeOffset deletedAtUtc)
        : base(entityId, position)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        StorageGuards.Utc(deletedAtUtc);
        Kind = kind;
        DeletedAtUtc = deletedAtUtc;
    }

    public DeletedEntityKind Kind { get; private set; }
    public DateTimeOffset DeletedAtUtc { get; private set; }
}
