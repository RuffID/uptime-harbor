namespace UptimeHarbor.Application.Cluster.Models;

public record AdministrationObserverDto(Guid NodeId, string Status, DateTimeOffset? CheckedAtUtc,
    string? Failure, string? EndpointName, DateTimeOffset? StartedAtUtc, DateTimeOffset? MetadataAtUtc,
    long? UptimeSeconds);
