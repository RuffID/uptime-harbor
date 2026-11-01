namespace UptimeHarbor.Application.Abstractions.Persistence;

public record PendingBacklog(long Pending, long Confirmation, DateTimeOffset? OldestAtUtc);
