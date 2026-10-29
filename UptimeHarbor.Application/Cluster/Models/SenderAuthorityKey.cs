namespace UptimeHarbor.Application.Cluster.Models;

public record SenderAuthorityKey(Guid ClusterId, long Generation, long MembershipVersion,
    long Term, Guid LeaderNodeId, Guid SenderEpoch);
