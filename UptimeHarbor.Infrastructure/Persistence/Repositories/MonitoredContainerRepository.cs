using EFCoreLibrary.Abstractions.Database.Repository.Base;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Infrastructure.Persistence.Repositories;

public class MonitoredContainerRepository(
    IGetItemByPredicateRepository<MonitoredContainer, UptimeHarborDbContext> getRepository,
    ICreateItemRepository<MonitoredContainer, UptimeHarborDbContext> createRepository,
    IDeleteItemRepository<MonitoredContainer, UptimeHarborDbContext> deleteRepository)
    : IMonitoredContainerRepository
{
    public Task<List<MonitoredContainer>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        return getRepository.GetItemsByPredicateAsync(
            asNoTracking: true,
            ct: cancellationToken);
    }

    public Task<MonitoredContainer?> GetItemByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return getRepository.GetItemByPredicateAsync(
            container => container.Id == id,
            ct: cancellationToken);
    }

    public Task<List<MonitoredContainer>> GetByServerIdAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        return getRepository.GetItemsByPredicateAsync(
            container => container.ServerId == serverId,
            asNoTracking: true,
            ct: cancellationToken);
    }

    public void Create(MonitoredContainer item)
    {
        createRepository.Create(item);
    }

    public void CreateRange(IEnumerable<MonitoredContainer> entities)
    {
        createRepository.CreateRange(entities);
    }

    public void Delete(MonitoredContainer item)
    {
        deleteRepository.Delete(item);
    }

    public void DeleteRange(IEnumerable<MonitoredContainer> entities)
    {
        deleteRepository.DeleteRange(entities);
    }
}
