namespace UptimeHarbor.Application.Notifications.Models;

public record PendingNotificationDeliveryDto(
    Guid BindingId,
    Guid IncidentId,
    NotificationDeliveryKind Kind,
    NotificationRecipient Recipient,
    string Message);
