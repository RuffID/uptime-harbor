using Microsoft.Extensions.Logging;
using UptimeHarbor.Application.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster;

public class DeliveryAudit(ILogger<DeliveryAudit> logger) : IDeliveryAudit
{
    public void Accepted(Guid nodeId, Guid? incidentId, Guid deliveryId, Guid attemptId, long term, string outcome, string? category)
    {
        logger.LogInformation("Delivery result accepted for node {NodeId}, incident {IncidentId}, delivery {DeliveryId}, attempt {AttemptId}, term {Term}: {Outcome}, {Category}.",
            nodeId, incidentId, deliveryId, attemptId, term, outcome, category);
    }
}
