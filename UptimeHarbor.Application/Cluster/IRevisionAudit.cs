namespace UptimeHarbor.Application.Cluster;

public interface IRevisionAudit
{
    void Record(Guid operationId, Guid actorUserId, Guid nodeId, string outcome);
    void RecordAccount(Guid operationId, Guid actorUserId, Guid subjectUserId, Guid nodeId, string outcome)
    {
        Record(operationId, actorUserId, nodeId, outcome);
    }
}
