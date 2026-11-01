namespace UptimeHarbor.Domain.Cluster;

public enum OperationOutcome
{
    Committed,
    Conflict,
    Rejected,
    Cancelled,
    Replaced
}
