using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Application.Dashboard.Models;

public record DashboardServerReadRecord(
    Guid Id,
    string Name,
    string? Description,
    string? InternalName,
    MonitoringStatus Status,
    DateTimeOffset? LastCheckedAtUtc,
    DateTimeOffset? StartedAtUtc);
