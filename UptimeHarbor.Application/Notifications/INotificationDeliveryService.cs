using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Monitoring.Models;
using UptimeHarbor.Application.Notifications.Models;

namespace UptimeHarbor.Application.Notifications;

public interface INotificationDeliveryService
{
    Task<IReadOnlyList<PendingNotificationDeliveryDto>> GetPendingDeliveriesAsync(
        MonitoringStateChangedEvent monitoringEvent,
        CancellationToken cancellationToken = default);

    Task<ServiceResult> MarkDeliverySuccessfulAsync(
        Guid bindingId,
        Guid incidentId,
        NotificationDeliveryKind kind,
        CancellationToken cancellationToken = default);
}
