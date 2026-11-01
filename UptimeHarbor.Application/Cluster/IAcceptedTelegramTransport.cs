using UptimeHarbor.Application.Notifications.Models;

namespace UptimeHarbor.Application.Cluster;

/// <summary>Transport возвращает настоящий Telegram message_id; разрешение проверяет dispatcher.</summary>
public interface IAcceptedTelegramTransport
{
    Task<long> SendAcceptedAsync(NotificationRecipient recipient, string message, CancellationToken token);
}
