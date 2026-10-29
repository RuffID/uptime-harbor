using System.Collections.Immutable;

namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Служебная команда observer либо текущего coordinator, без пользовательского актора.</summary>
public record MonitoringCommand
{
    public required Guid StorageGeneration { get; init; }
    public long CoordinatorTerm { get; init; }
    public ImmutableArray<MonitoringObservation> Observations { get; init; } = [];
    public DateTimeOffset? EvaluationTimeUtc { get; init; }
    public string? FrontierDigest { get; init; }
    public bool Historical { get; init; }
    public long CutIndex { get; init; }
}
