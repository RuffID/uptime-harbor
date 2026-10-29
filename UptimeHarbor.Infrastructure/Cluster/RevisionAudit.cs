using Microsoft.Extensions.Logging;
using UptimeHarbor.Application.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster;

public class RevisionAudit(ILogger<RevisionAudit> logger) : IRevisionAudit
{
    public void Record(Guid operationId, Guid actorUserId, Guid nodeId, string outcome)
    {
        if (outcome is not ("LocalPending" or "Committed" or "Conflict" or "Rejected" or "Cancelled" or "Replaced" or "AlreadyDecided" or "Denied"))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        logger.LogInformation("Cluster command {OperationId} by {ActorUserId} from {NodeId}: {Outcome}.",
            operationId, actorUserId, nodeId, outcome);
    }
}
