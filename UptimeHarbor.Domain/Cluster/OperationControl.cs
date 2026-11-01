namespace UptimeHarbor.Domain.Cluster;

public class OperationControl : ProjectionRecord
{
    private OperationControl()
    {
    }

    public OperationControl(Guid controlId, JournalPosition position, Guid targetOperationId,
        string targetDigest, Guid expectedHead, ControlKind kind, Guid? replacementOperationId)
        : base(controlId, position)
    {
        StorageGuards.Id(targetOperationId, nameof(targetOperationId));
        StorageGuards.Id(expectedHead, nameof(expectedHead));
        if (!Enum.IsDefined(kind) || replacementOperationId == Guid.Empty
            || (kind == ControlKind.Replace) != replacementOperationId.HasValue
            || replacementOperationId == targetOperationId)
        {
            throw new ArgumentException("Invalid operation control.");
        }

        TargetOperationId = targetOperationId;
        TargetDigest = StorageGuards.Digest(targetDigest);
        ExpectedHead = expectedHead;
        Kind = kind;
        ReplacementOperationId = replacementOperationId;
    }

    public Guid TargetOperationId { get; private set; }
    public string TargetDigest { get; private set; } = string.Empty;
    public Guid ExpectedHead { get; private set; }
    public ControlKind Kind { get; private set; }
    public Guid? ReplacementOperationId { get; private set; }
}
