using UptimeHarbor.Application.Abstractions.Persistence.Base;
using UptimeHarbor.Domain.Notifications;

namespace UptimeHarbor.Application.Abstractions.Persistence;

public interface ITelegramChatRepository :
    ICreateItemRepository<TelegramChat>,
    IDeleteItemRepository<TelegramChat>,
    IGetItemByIdRepository<TelegramChat, Guid>
{
    Task<IReadOnlyList<TelegramChat>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task<TelegramChat?> GetByTelegramChatIdAsync(
        long telegramChatId,
        CancellationToken cancellationToken = default);
}
