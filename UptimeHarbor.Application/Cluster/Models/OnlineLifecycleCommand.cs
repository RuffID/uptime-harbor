namespace UptimeHarbor.Application.Cluster.Models;

public record OnlineLifecycleCommand(int Version, Guid StorageGeneration, OnlineGrant Grant,
    DateTimeOffset? CoordinatorAtUtc = null, long CoordinatorTerm = 0);
