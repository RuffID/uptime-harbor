namespace UptimeHarbor.Application.Cluster.Models;

public record MonitoringStream(Guid Id, Guid TargetId, Guid ObserverNodeId, Guid StorageGeneration,
    Guid CheckGeneration, long Sequence, DateTimeOffset WatermarkUtc, Guid BatchId,
    int ConsecutiveFailures, long Index, long Term);
