namespace UptimeHarbor.Application.Cluster.Models;

public record OperationalNode(Guid NodeId, Guid StorageGeneration, string Address, string RaftMemberId,
    bool Voting, bool Activated, bool Removed, long Revision);
