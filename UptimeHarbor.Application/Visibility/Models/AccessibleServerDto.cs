using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Application.Visibility.Models;

public record AccessibleServerDto(
    Guid Id,
    string EndpointUrl,
    string? Description,
    string? LastKnownName,
    MonitoringStatus Status,
    DateTimeOffset? LastCheckedAtUtc,
    IReadOnlyList<AccessibleContainerDto> Containers);
