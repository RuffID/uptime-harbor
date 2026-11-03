using System.Collections.Immutable;

namespace UptimeHarbor.Application.Cluster.Models;

public record SenderOutage(int Version, Guid OutageId, long AssignmentRevision, string State,
    DateTimeOffset DetectedAtUtc, ImmutableDictionary<Guid, long> ChatCoverage);
