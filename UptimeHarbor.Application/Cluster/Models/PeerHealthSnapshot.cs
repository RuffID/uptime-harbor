namespace UptimeHarbor.Application.Cluster.Models;

public record PeerHealthSnapshot(Guid NodeId, Guid BootId, long Term, Guid? LeaderNodeId,
    long MembershipVersion, long CommittedIndex, long AppliedIndex, bool Ready,
    double SecondsWithoutReadyLeader, IReadOnlyDictionary<string, HealthResult> Components,
    IReadOnlySet<Guid>? ConfirmedPeers = null, ReplicationProgress? Replication = null,
    MonitoringProgress? Monitoring = null);
