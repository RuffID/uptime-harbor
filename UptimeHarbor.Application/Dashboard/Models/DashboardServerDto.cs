using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Application.Dashboard.Models;

public record DashboardServerDto(
    Guid Id,
    string Name,
    string? InternalName,
    string? Description,
    MonitoringStatus Status,
    DateTimeOffset? LastCheckedAtUtc,
    DateTimeOffset? StartedAtUtc,
    long? UptimeSeconds,
    IReadOnlyList<DashboardContainerDto> Containers);
