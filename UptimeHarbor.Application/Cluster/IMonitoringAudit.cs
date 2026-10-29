namespace UptimeHarbor.Application.Cluster;

public interface IMonitoringAudit
{
    void Transition(Guid nodeId, Guid incidentId, Guid eventId, Guid targetId, long acceptedIndex);
}
