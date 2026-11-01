namespace UptimeHarbor.Application.Cluster;

public interface IClusterAudit
{
    void Membership(Guid userId, Guid nodeId, Guid changeId, string outcome);
}
