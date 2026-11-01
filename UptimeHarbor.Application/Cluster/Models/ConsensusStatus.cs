namespace UptimeHarbor.Application.Cluster.Models;

public record ConsensusStatus(Guid ClusterId, long Generation, Guid NodeId, long Term,
    long MembershipVersion, bool IsLeader, long CommittedIndex, IReadOnlySet<Guid> VotingNodes,
    CancellationToken LeadershipToken, CancellationToken ConsensusToken);
