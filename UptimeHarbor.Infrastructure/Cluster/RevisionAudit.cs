using Microsoft.Extensions.Logging;
using UptimeHarbor.Application.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster;

public class RevisionAudit(ILogger<RevisionAudit> logger) : IRevisionAudit
{
    public void RecordAccount(Guid operationId, Guid actorUserId, Guid subjectUserId, Guid nodeId, string outcome)
    {
        ValidateOutcome(outcome);
        logger.LogInformation("Cluster account command {OperationId} by {ActorUserId} for {SubjectUserId} from {NodeId}: {Outcome}.",
            operationId, actorUserId, subjectUserId, nodeId, outcome);
    }

    public void Record(Guid operationId, Guid actorUserId, Guid nodeId, string outcome)
    {
        ValidateOutcome(outcome);
        logger.LogInformation("Cluster command {OperationId} by {ActorUserId} from {NodeId}: {Outcome}.",
            operationId, actorUserId, nodeId, outcome);
    }

    private static void ValidateOutcome(string outcome)
    {
        if (outcome is not ("LocalPending" or "Committed" or "Conflict" or "Rejected" or "Cancelled" or "Replaced" or "AlreadyDecided" or "Denied"))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }
    }
}
