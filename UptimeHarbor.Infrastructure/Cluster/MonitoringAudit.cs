using Microsoft.Extensions.Logging;
using UptimeHarbor.Application.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster;

public class MonitoringAudit(ILogger<MonitoringAudit> logger) : IMonitoringAudit
{
    public void Transition(Guid nodeId, Guid incidentId, Guid eventId, Guid targetId, long acceptedIndex)
    {
        logger.LogInformation("MonitoringCoordinator on {NodeId} accepted transition {EventId} for {IncidentId}, target {TargetId}, index {AcceptedIndex}.",
            nodeId, eventId, incidentId, targetId, acceptedIndex);
    }
}
