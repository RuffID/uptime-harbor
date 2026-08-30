using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Application.ObjectAdministration.Models;

public record AdminServerDto(
    Guid Id,
    string EndpointUrl,
    string? Description,
    bool IsMonitoringEnabled,
    string? LastKnownName,
    MonitoringStatus Status,
    int ConsecutiveFailures,
    long Version);
