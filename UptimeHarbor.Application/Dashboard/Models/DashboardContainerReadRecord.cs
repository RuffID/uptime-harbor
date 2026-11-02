using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Application.Dashboard.Models;

public record DashboardContainerReadRecord(
    Guid Id,
    Guid? ServerId,
    string? Description,
    string? LastKnownName,
    MonitoringStatus Status,
    DateTimeOffset? LastCheckedAtUtc,
    DateTimeOffset? StartedAtUtc,
    string? Name = null);
