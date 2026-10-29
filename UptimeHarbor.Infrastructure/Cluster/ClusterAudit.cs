using Microsoft.Extensions.Logging;
using UptimeHarbor.Application.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster;

public class ClusterAudit(ILogger<ClusterAudit> logger) : IClusterAudit
{
    public void Membership(Guid userId, Guid nodeId, Guid changeId, string outcome)
    {
        if (outcome is not ("Denied" or "Unconfirmed" or "Unknown" or "Committed" or "Cancelled"))
        {
            throw new ArgumentException("Unknown membership outcome.", nameof(outcome));
        }

        logger.LogInformation("Membership change {ChangeId} by {ActorUserId} for {NodeId}: {Outcome}.",
            changeId, userId, nodeId, outcome);
    }
}
