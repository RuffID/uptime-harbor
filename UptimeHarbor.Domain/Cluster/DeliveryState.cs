namespace UptimeHarbor.Domain.Cluster;

public enum DeliveryState
{
    Planned,
    AttemptStarted,
    Succeeded,
    RetryDue,
    Failed,
    Cancelled,
    SuppressedBySummary
}
