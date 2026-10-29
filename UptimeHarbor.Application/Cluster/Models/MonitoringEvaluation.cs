using System.Collections.Immutable;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Metadata остаются у observer; Unknown и coverage не подменяют подтверждённый отказ.</summary>
public record MonitoringEvaluation(Guid TargetId, Guid CheckGeneration, AvailabilityState Availability,
    CoverageState Coverage, DateTimeOffset AtUtc, string FrontierDigest,
    ImmutableArray<Guid> ProblemNodes, ImmutableArray<Guid> MissingNodes,
    ImmutableDictionary<Guid, MonitoringObservation> Metadata, long Index, long Term)
{
    public ImmutableDictionary<Guid, string> Failures { get; init; } = ImmutableDictionary<Guid, string>.Empty;
}
