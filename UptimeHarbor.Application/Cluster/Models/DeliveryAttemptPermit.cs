namespace UptimeHarbor.Application.Cluster.Models;

public record DeliveryAttemptPermit(SenderAuthorityKey Key, Guid AttemptId, long AppliedIndex);
