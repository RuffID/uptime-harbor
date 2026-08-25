using EFCoreLibrary.Abstractions.Database.Repository.Base;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Domain.Notifications;

namespace UptimeHarbor.Infrastructure.Persistence.Repositories;

public class TelegramChatRepository(
    IGetItemByPredicateRepository<TelegramChat, UptimeHarborDbContext> getRepository,
    ICreateItemRepository<TelegramChat, UptimeHarborDbContext> createRepository,
    IDeleteItemRepository<TelegramChat, UptimeHarborDbContext> deleteRepository)
    : ITelegramChatRepository
{
    public Task<TelegramChat?> GetItemByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return getRepository.GetItemByPredicateAsync(
            chat => chat.Id == id,
            ct: cancellationToken);
    }

    public Task<List<TelegramChat>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        return getRepository.GetItemsByPredicateAsync(
            asNoTracking: true,
            ct: cancellationToken);
    }

    public void Create(TelegramChat item)
    {
        createRepository.Create(item);
    }

    public void CreateRange(IEnumerable<TelegramChat> entities)
    {
        createRepository.CreateRange(entities);
    }

    public void Delete(TelegramChat item)
    {
        deleteRepository.Delete(item);
    }

    public void DeleteRange(IEnumerable<TelegramChat> entities)
    {
        deleteRepository.DeleteRange(entities);
    }
}
