using UptimeHarbor.Application.Abstractions.Persistence.Base;
using UptimeHarbor.Domain.Notifications;

namespace UptimeHarbor.Application.Abstractions.Persistence;

public interface INotificationBindingRepository :
    ICreateItemRepository<NotificationBinding>,
    IDeleteItemRepository<NotificationBinding>,
    IGetItemByIdRepository<NotificationBinding, Guid>
{
    Task<IReadOnlyList<NotificationBinding>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationBinding>> GetByChatIdAsync(
        Guid telegramChatId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationBinding>> GetByServerIdAsync(
        Guid serverId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationBinding>> GetByContainerIdAsync(
        Guid containerId,
        CancellationToken cancellationToken = default);
}
