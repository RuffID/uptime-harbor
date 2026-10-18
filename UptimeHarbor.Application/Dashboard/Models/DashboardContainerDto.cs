using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Application.Dashboard.Models;

public record DashboardContainerDto(
    Guid Id,
    Guid? ServerId,
    string DisplayName,
    string? Description,
    MonitoringStatus Status,
    DateTimeOffset? LastCheckedAtUtc,
    DateTimeOffset? StartedAtUtc,
    long? UptimeSeconds);
