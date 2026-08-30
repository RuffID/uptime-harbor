using EFCoreLibrary.Abstractions.Database.Repository.Base;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Domain.Notifications;

namespace UptimeHarbor.Infrastructure.Persistence.Repositories;

public class NotificationBindingRepository(
    IGetItemByPredicateRepository<NotificationBinding, UptimeHarborDbContext> getRepository,
    ICreateItemRepository<NotificationBinding, UptimeHarborDbContext> createRepository,
    IDeleteItemRepository<NotificationBinding, UptimeHarborDbContext> deleteRepository)
    : INotificationBindingRepository
{
    public Task<NotificationBinding?> GetItemByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return getRepository.GetItemByPredicateAsync(
            binding => binding.Id == id,
            ct: cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationBinding>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        List<NotificationBinding> bindings = await getRepository.GetItemsByPredicateAsync(
            asNoTracking: true,
            ct: cancellationToken);
        return bindings;
    }

    public async Task<IReadOnlyList<NotificationBinding>> GetByChatIdAsync(
        Guid telegramChatId,
        CancellationToken cancellationToken = default)
    {
        List<NotificationBinding> bindings = await getRepository.GetItemsByPredicateAsync(
            binding => binding.TelegramChatId == telegramChatId,
            asNoTracking: true,
            ct: cancellationToken);
        return bindings;
    }

    public async Task<IReadOnlyList<NotificationBinding>> GetByServerIdAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        List<NotificationBinding> bindings = await getRepository.GetItemsByPredicateAsync(
            binding => binding.ServerId == serverId,
            asNoTracking: true,
            ct: cancellationToken);
        return bindings;
    }

    public async Task<IReadOnlyList<NotificationBinding>> GetByContainerIdAsync(
        Guid containerId,
        CancellationToken cancellationToken = default)
    {
        List<NotificationBinding> bindings = await getRepository.GetItemsByPredicateAsync(
            binding => binding.ContainerId == containerId,
            asNoTracking: true,
            ct: cancellationToken);
        return bindings;
    }

    public void Create(NotificationBinding item)
    {
        createRepository.Create(item);
    }

    public void CreateRange(IEnumerable<NotificationBinding> entities)
    {
        createRepository.CreateRange(entities);
    }

    public void Delete(NotificationBinding item)
    {
        deleteRepository.Delete(item);
    }

    public void DeleteRange(IEnumerable<NotificationBinding> entities)
    {
        deleteRepository.DeleteRange(entities);
    }
}
