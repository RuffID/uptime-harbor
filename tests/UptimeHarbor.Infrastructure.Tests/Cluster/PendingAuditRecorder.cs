using UptimeHarbor.Application.Cluster;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

internal class PendingAuditRecorder : IRevisionAudit
{
    public List<string> Outcomes { get; } = [];
    public List<(Guid Operation, Guid Actor, Guid Subject, Guid Node, string Outcome)> Accounts { get; } = [];

    public void RecordAccount(Guid operationId, Guid actorUserId, Guid subjectUserId, Guid nodeId, string outcome)
    {
        Accounts.Add((operationId, actorUserId, subjectUserId, nodeId, outcome));
        Outcomes.Add(outcome);
    }

    public void Record(Guid operationId, Guid actorUserId, Guid nodeId, string outcome)
    {
        Outcomes.Add(outcome);
    }
}
