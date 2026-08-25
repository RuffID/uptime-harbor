using UptimeHarbor.Application.Monitoring;

namespace UptimeHarbor.Application.Notifications.Models;

public record NotificationTargetDto(
    Guid Id,
    MonitoringObjectKind Kind,
    Guid? ServerId,
    string DisplayName);
