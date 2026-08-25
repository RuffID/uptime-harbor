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

    public Task<List<NotificationBinding>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        return getRepository.GetItemsByPredicateAsync(
            asNoTracking: true,
            ct: cancellationToken);
    }

    public Task<List<NotificationBinding>> GetByChatIdAsync(
        Guid telegramChatId,
        CancellationToken cancellationToken = default)
    {
        return getRepository.GetItemsByPredicateAsync(
            binding => binding.TelegramChatId == telegramChatId,
            asNoTracking: true,
            ct: cancellationToken);
    }

    public Task<List<NotificationBinding>> GetByServerIdAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        return getRepository.GetItemsByPredicateAsync(
            binding => binding.ServerId == serverId,
            asNoTracking: true,
            ct: cancellationToken);
    }

    public Task<List<NotificationBinding>> GetByContainerIdAsync(
        Guid containerId,
        CancellationToken cancellationToken = default)
    {
        return getRepository.GetItemsByPredicateAsync(
            binding => binding.ContainerId == containerId,
            asNoTracking: true,
            ct: cancellationToken);
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
