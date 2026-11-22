namespace UptimeHarbor.Application.Cluster.Models;

public record ClusterNodeDiagnosticDto(Guid NodeId, string Connection, double? AgeSeconds,
    DateTimeOffset? LastContactAtUtc, string Database, string Role, long? AppliedIndex, long? Lag)
{
    public int? Number { get; init; }
    public bool CanAssignSender { get; init; }
}
