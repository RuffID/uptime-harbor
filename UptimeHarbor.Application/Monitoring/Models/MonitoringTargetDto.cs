namespace UptimeHarbor.Application.Monitoring.Models;

public record MonitoringTargetDto(
    Guid Id,
    MonitoringObjectKind ObjectKind,
    string EndpointUrl);
