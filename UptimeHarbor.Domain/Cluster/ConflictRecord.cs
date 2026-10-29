namespace UptimeHarbor.Domain.Cluster;

public class ConflictRecord : ProjectionRecord
{
    private ConflictRecord()
    {
    }

    public ConflictRecord(Guid id, JournalPosition position, Guid operationId, Guid entityId,
        long baseRevision, long firstAcceptedRevision, string protectedBasePayload,
        string protectedAcceptedPayload, string protectedProposedPayload, DateTimeOffset createdAtUtc)
        : base(id, position)
    {
        StorageGuards.Id(operationId, nameof(operationId));
        StorageGuards.Id(entityId, nameof(entityId));
        ArgumentOutOfRangeException.ThrowIfNegative(baseRevision);
        if (firstAcceptedRevision <= baseRevision)
        {
            throw new ArgumentOutOfRangeException(nameof(firstAcceptedRevision));
        }

        StorageGuards.Utc(createdAtUtc);
        OperationId = operationId;
        EntityId = entityId;
        BaseRevision = baseRevision;
        FirstAcceptedRevision = firstAcceptedRevision;
        ProtectedBasePayload = StorageGuards.Payload(protectedBasePayload);
        ProtectedAcceptedPayload = StorageGuards.Payload(protectedAcceptedPayload);
        ProtectedProposedPayload = StorageGuards.Payload(protectedProposedPayload);
        CreatedAtUtc = createdAtUtc;
    }

    public Guid OperationId { get; private set; }
    public Guid EntityId { get; private set; }
    public long BaseRevision { get; private set; }
    public long FirstAcceptedRevision { get; private set; }
    public string ProtectedBasePayload { get; private set; } = string.Empty;
    public string ProtectedAcceptedPayload { get; private set; } = string.Empty;
    public string ProtectedProposedPayload { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? ResolvedAtUtc { get; private set; }
    public Guid? ResolutionOperationId { get; private set; }

    public void Resolve(Guid operationId, DateTimeOffset resolvedAtUtc)
    {
        StorageGuards.Id(operationId, nameof(operationId));
        StorageGuards.Utc(resolvedAtUtc);
        if (ResolvedAtUtc.HasValue || resolvedAtUtc < CreatedAtUtc)
        {
            throw new InvalidOperationException("Conflict resolution is invalid.");
        }

        ResolutionOperationId = operationId;
        ResolvedAtUtc = resolvedAtUtc;
    }
}
