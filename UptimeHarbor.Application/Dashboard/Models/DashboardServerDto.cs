using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Application.Dashboard.Models;

public record DashboardServerDto(
    Guid Id,
    string DisplayName,
    string? Description,
    MonitoringStatus Status,
    DateTimeOffset? LastCheckedAtUtc,
    IReadOnlyList<DashboardContainerDto> Containers);
