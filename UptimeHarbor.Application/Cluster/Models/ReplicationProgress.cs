namespace UptimeHarbor.Application.Cluster.Models;

public record ReplicationProgress(long Pending, long AwaitingConfirmation, long Rejected,
    long AppliedIndex, long CommittedIndex, long Backlog, DateTimeOffset? OldestPendingAtUtc,
    string? DelayReason);
