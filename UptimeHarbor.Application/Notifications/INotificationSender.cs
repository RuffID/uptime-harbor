using UptimeHarbor.Application.Notifications.Models;

namespace UptimeHarbor.Application.Notifications;

public interface INotificationSender
{
    Task SendAsync(
        NotificationRecipient recipient,
        string message,
        CancellationToken cancellationToken = default);
}
