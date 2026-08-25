using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Application.Visibility.Models;

public record AccessibleContainerDto(
    Guid Id,
    Guid ServerId,
    string EndpointUrl,
    string? Description,
    string? LastKnownName,
    MonitoringStatus Status,
    DateTimeOffset? LastCheckedAtUtc,
    DateTimeOffset? StartedAtUtc);
