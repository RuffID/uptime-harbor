namespace UptimeHarbor.Application.Cluster;

public interface IRevisionAudit
{
    void Record(Guid operationId, Guid actorUserId, Guid nodeId, string outcome);
}
