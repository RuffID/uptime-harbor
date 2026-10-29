using System.Collections.Immutable;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Models;

public record MonitoringIncident(Guid Id, Guid TargetId, Guid CheckGeneration, IncidentKind Kind,
    string TransitionKey, DateTimeOffset OpenedAtUtc, DateTimeOffset? CompletedAtUtc,
    IncidentCompletion? Completion, string StateKey, long EventSequence,
    ImmutableArray<Guid> ProblemNodes, long Index, long Term)
{
    public ImmutableDictionary<Guid, string> Failures { get; init; } = ImmutableDictionary<Guid, string>.Empty;
}
