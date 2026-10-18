using EFCoreLibrary.Abstractions.Database.Repository.Base;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Infrastructure.Persistence.Repositories;

public class MonitoredContainerRepository(
    IContextGetItemByPredicateRepository<MonitoredContainer, UptimeHarborDbContext> getRepository,
    IContextCreateItemRepository<MonitoredContainer, UptimeHarborDbContext> createRepository,
    IContextDeleteItemRepository<MonitoredContainer, UptimeHarborDbContext> deleteRepository)
    : IMonitoredContainerRepository
{
    public async Task<IReadOnlyList<MonitoredContainer>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        List<MonitoredContainer> containers = await getRepository.GetItemsByPredicateAsync(
            asNoTracking: true,
            ct: cancellationToken);
        return containers;
    }

    public async Task<IReadOnlyList<MonitoredContainer>> GetEnabledAsync(
        CancellationToken cancellationToken = default)
    {
        List<MonitoredContainer> containers = await getRepository.GetItemsByPredicateAsync(
            container => container.IsMonitoringEnabled,
            asNoTracking: true,
            ct: cancellationToken);
        return containers;
    }

    public Task<MonitoredContainer?> GetItemByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return getRepository.GetItemByPredicateAsync(
            container => container.Id == id,
            ct: cancellationToken);
    }

    public async Task<IReadOnlyList<MonitoredContainer>> GetByServerIdAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        List<MonitoredContainer> containers = await getRepository.GetItemsByPredicateAsync(
            container => container.ServerId == serverId,
            asNoTracking: true,
            ct: cancellationToken);
        return containers;
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
