using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Notifications;
using UptimeHarbor.Application.Notifications.Models;
using UptimeHarbor.Infrastructure.Telegram;

namespace UptimeHarbor.Infrastructure.Cluster;

public class ClusterNotificationSender(IOptions<ClusterOptions> options, TelegramHttpNotificationSender legacy)
    : INotificationSender
{
    public Task SendAsync(NotificationRecipient recipient, string message, CancellationToken cancellationToken = default)
    {
        if (options.Value.Enabled)
        {
            // Legacy вызов не содержит принятого AttemptId; не создавать фиктивное разрешение.
            throw new NotificationDeliveryException(NotificationDeliveryFailure.Unavailable);
        }

        return legacy.SendAsync(recipient, message, cancellationToken);
    }
}
