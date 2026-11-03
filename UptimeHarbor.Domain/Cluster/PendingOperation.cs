namespace UptimeHarbor.Domain.Cluster;

public class PendingOperation
{
    private PendingOperation()
    {
    }

    public PendingOperation(ClusterIdentity cluster, CommandIdentity command, string kind, Guid entityId,
        long baseRevision, long actorUserRevision, string digest, string protectedPayload,
        DateTimeOffset createdAtUtc, int protocolVersion = 1, int schemaVersion = 1)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(command);
        StorageGuards.Id(entityId, nameof(entityId));
        StorageGuards.Utc(createdAtUtc);
        ArgumentOutOfRangeException.ThrowIfNegative(baseRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(actorUserRevision);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(protocolVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schemaVersion);
        Id = command.OperationId;
        ClusterId = cluster.ClusterId;
        ClusterGeneration = cluster.Generation;
        OriginNodeId = command.OriginNodeId;
        ActorUserId = command.ActorUserId;
        SystemSource = command.SystemSource;
        Kind = StorageGuards.Text(kind, 64, nameof(kind));
        EntityId = entityId;
        BaseRevision = baseRevision;
        ActorUserRevision = actorUserRevision;
        Digest = StorageGuards.Digest(digest);
        ProtectedPayload = StorageGuards.Payload(protectedPayload);
        CreatedAtUtc = createdAtUtc;
        ProtocolVersion = protocolVersion;
        SchemaVersion = schemaVersion;
    }

    public Guid Id { get; private set; }
    public Guid ClusterId { get; private set; }
    public long ClusterGeneration { get; private set; }
    public Guid OriginNodeId { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public string? SystemSource { get; private set; }
    public Guid EntityId { get; private set; }
    public string Kind { get; private set; } = string.Empty;
    public long BaseRevision { get; private set; }
    public long ActorUserRevision { get; private set; }
    public string Digest { get; private set; } = string.Empty;
    public string ProtectedPayload { get; private set; } = string.Empty;
    public int PayloadVersion { get; private set; } = 1;
    public int ProtocolVersion { get; private set; } = 1;
    public int SchemaVersion { get; private set; } = 1;
    public PendingState State { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }
    public int SubmissionAttempts { get; private set; }
    public Guid? RequestedControlId { get; private set; }
    public ControlKind? RequestedControlKind { get; private set; }
    public Guid? ReplacementOperationId { get; private set; }

    public void MarkTransmissionStarted(DateTimeOffset nextAttemptAtUtc)
    {
        StorageGuards.Utc(nextAttemptAtUtc);
        if (State is PendingState.NeedsUserConfirmation or PendingState.Resolved)
        {
            throw new InvalidOperationException("This pending operation cannot be submitted.");
        }

        State = PendingState.AwaitingDecision;
        SubmissionAttempts = checked(SubmissionAttempts + 1);
        NextAttemptAtUtc = nextAttemptAtUtc;
    }

    public void RequestControl(Guid controlId, ControlKind kind, Guid? replacementOperationId)
    {
        StorageGuards.Id(controlId, nameof(controlId));
        if (State == PendingState.Resolved || !Enum.IsDefined(kind)
            || replacementOperationId == Guid.Empty || replacementOperationId == Id
            || (kind == ControlKind.Replace) != replacementOperationId.HasValue)
        {
            throw new InvalidOperationException("Invalid pending control.");
        }

        RequestedControlId = controlId;
        RequestedControlKind = kind;
        ReplacementOperationId = replacementOperationId;
        // Намерение ещё не подтверждает отмену или замену.
    }

    public void RequireConfirmationAfterRestore()
    {
        if (State != PendingState.Resolved)
        {
            State = PendingState.NeedsUserConfirmation;
            NextAttemptAtUtc = null;
        }
    }

    public void Reconcile(OperationDecision decision)
    {
        if (decision.Id != Id || decision.ClusterId != ClusterId || decision.ClusterGeneration != ClusterGeneration)
        {
            throw new InvalidOperationException("Operation identity does not match.");
        }

        decision.EnsureSameRequest(Digest);
        State = PendingState.Resolved;
        NextAttemptAtUtc = null;
    }
}
