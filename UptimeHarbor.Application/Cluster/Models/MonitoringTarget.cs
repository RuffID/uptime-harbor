using System.Collections.Immutable;

namespace UptimeHarbor.Application.Cluster.Models;

public record MonitoringTarget(Guid Id, Guid CheckGeneration, long ConfigRevision, bool IsNode,
    ImmutableArray<Guid> Observers, int FailureThreshold, int IntervalSeconds, int TimeoutSeconds,
    DateTimeOffset AssignedAtUtc);
