namespace UptimeHarbor.Application.Cluster;

public interface IDeliveryAudit
{
    void Accepted(Guid nodeId, Guid? incidentId, Guid deliveryId, Guid attemptId, long term, string outcome, string? category);
}
