namespace UptimeHarbor.Domain.Cluster;

public enum AttemptOutcome
{
    Unknown,
    Succeeded,
    Failed,
    CancelledBeforeTransport
}
