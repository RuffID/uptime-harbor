using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Models;

public record CommandResult(Guid OperationId, string Digest, OperationOutcome Outcome,
    long Index, long Term, long? AppliedRevision = null, string? Reason = null,
    Guid? ConflictId = null, Guid? ReplacedBy = null, long ActorRevision = 0)
{
    public Guid? SubjectUserId { get; init; }
}
