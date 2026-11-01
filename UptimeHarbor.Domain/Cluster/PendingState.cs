namespace UptimeHarbor.Domain.Cluster;

public enum PendingState
{
    LocalPending,
    AwaitingDecision,
    NeedsUserConfirmation,
    Resolved
}
