namespace UptimeHarbor.Domain.Cluster;

public class OperationDecision : ProjectionRecord
{
    private OperationDecision()
    {
    }

    public OperationDecision(Guid operationId, string digest, OperationOutcome outcome,
        JournalPosition position, long? appliedRevision = null, string? reasonCode = null,
        Guid? conflictId = null, Guid? replacedBy = null) : base(operationId, position)
    {
        if (!Enum.IsDefined(outcome) || appliedRevision <= 0 || conflictId == Guid.Empty || replacedBy == Guid.Empty)
        {
            throw new ArgumentException("Invalid operation result.");
        }

        if ((outcome == OperationOutcome.Replaced) != replacedBy.HasValue
            || (outcome == OperationOutcome.Conflict) != conflictId.HasValue)
        {
            throw new ArgumentException("The terminal outcome requires its corresponding reference.");
        }

        Digest = StorageGuards.Digest(digest);
        Outcome = outcome;
        AppliedRevision = appliedRevision;
        if (reasonCode is not null)
        {
            StorageGuards.Text(reasonCode, 64, nameof(reasonCode));
        }

        ReasonCode = reasonCode;
        ConflictId = conflictId;
        ReplacedBy = replacedBy;
    }

    public string Digest { get; private set; } = string.Empty;
    public OperationOutcome Outcome { get; private set; }
    public long? AppliedRevision { get; private set; }
    public string? ReasonCode { get; private set; }
    public Guid? ConflictId { get; private set; }
    public Guid? ReplacedBy { get; private set; }

    public void EnsureSameRequest(string digest)
    {
        if (!string.Equals(Digest, StorageGuards.Digest(digest), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("IdempotencyKeyReuse");
        }
    }
}
