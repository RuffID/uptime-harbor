namespace UptimeHarbor.Domain.Cluster;

public enum MembershipChangeState
{
    Requested,
    CatchingUp,
    ProtocolCommitted,
    Activated,
    Removed
}
