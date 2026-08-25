using UptimeHarbor.Application.Abstractions.Persistence.Base;
using UptimeHarbor.Domain.Notifications;

namespace UptimeHarbor.Application.Abstractions.Persistence;

public interface INotificationBindingRepository :
    ICreateItemRepository<NotificationBinding>,
    IDeleteItemRepository<NotificationBinding>
{
    Task<List<NotificationBinding>> GetByChatIdAsync(
        Guid telegramChatId,
        CancellationToken cancellationToken = default);
}
