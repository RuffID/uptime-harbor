namespace UptimeHarbor.Application.Cluster.Models;

public record AcceptedClusterState(bool AccountStateConfirmed, bool SnapshotConfirmed,
    bool CaughtUp, bool Activated, bool ManuallyEnabled, long AppliedIndex, long MembershipVersion,
    Guid SenderEpoch, bool DeliveryStateConfirmed, bool MembershipChangePending = false)
{
    public static AcceptedClusterState Unknown { get; } = new(false, false, false, false,
        false, 0, -1, Guid.Empty, false);
}
