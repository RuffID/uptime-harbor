using UptimeHarbor.Application.Abstractions.Persistence.Base;
using UptimeHarbor.Domain.Notifications;

namespace UptimeHarbor.Application.Abstractions.Persistence;

public interface INotificationBindingRepository :
    ICreateItemRepository<NotificationBinding>,
    IDeleteItemRepository<NotificationBinding>,
    IGetItemByIdRepository<NotificationBinding, Guid>
{
    Task<List<NotificationBinding>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task<List<NotificationBinding>> GetByChatIdAsync(
        Guid telegramChatId,
        CancellationToken cancellationToken = default);

    Task<List<NotificationBinding>> GetByServerIdAsync(
        Guid serverId,
        CancellationToken cancellationToken = default);

    Task<List<NotificationBinding>> GetByContainerIdAsync(
        Guid containerId,
        CancellationToken cancellationToken = default);
}
