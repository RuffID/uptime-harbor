using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Application.Dashboard.Models;

public record DashboardServerReadRecord(
    Guid Id,
    string? Description,
    string? LastKnownName,
    MonitoringStatus Status,
    DateTimeOffset? LastCheckedAtUtc);
