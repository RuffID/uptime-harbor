using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Application.ObjectAdministration.Models;

public record AdminContainerDto(
    Guid Id,
    Guid? ServerId,
    string EndpointUrl,
    string? Description,
    bool IsMonitoringEnabled,
    string? LastKnownName,
    MonitoringStatus Status,
    int ConsecutiveFailures,
    long Version);
