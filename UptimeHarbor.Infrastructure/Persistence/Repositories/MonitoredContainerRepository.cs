using EFCoreLibrary.Abstractions.Database.Repository.Base;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Infrastructure.Persistence.Repositories;

public class MonitoredContainerRepository(
    IContextGetItemByPredicateRepository<MonitoredServer, UptimeHarborDbContext> getRepository,
    IContextCreateItemRepository<MonitoredServer, UptimeHarborDbContext> createRepository,
    IContextDeleteItemRepository<MonitoredServer, UptimeHarborDbContext> deleteRepository)
    : IMonitoredContainerRepository
{
    public async Task<IReadOnlyList<MonitoredServer>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        List<MonitoredServer> containers = await getRepository.GetItemsByPredicateAsync(
            container => container.Type == ServerType.Application,
            asNoTracking: true,
            ct: cancellationToken);
        return containers;
    }

    public async Task<IReadOnlyList<MonitoredServer>> GetEnabledAsync(
        CancellationToken cancellationToken = default)
    {
        List<MonitoredServer> containers = await getRepository.GetItemsByPredicateAsync(
            container => container.Type == ServerType.Application && container.IsMonitoringEnabled,
            asNoTracking: true,
            ct: cancellationToken);
        return containers;
    }

    public Task<MonitoredServer?> GetItemByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return getRepository.GetItemByPredicateAsync(
            container => container.Id == id && container.Type == ServerType.Application,
            ct: cancellationToken);
    }

    public async Task<IReadOnlyList<MonitoredServer>> GetByServerIdAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        List<MonitoredServer> containers = await getRepository.GetItemsByPredicateAsync(
            container => container.Type == ServerType.Application && container.ServerId == serverId,
            asNoTracking: true,
            ct: cancellationToken);
        return containers;
    }

    public void Create(MonitoredServer item)
    {
        createRepository.Create(item);
    }

    public void CreateRange(IEnumerable<MonitoredServer> entities)
    {
        createRepository.CreateRange(entities);
    }

    public void Delete(MonitoredServer item)
    {
        deleteRepository.Delete(item);
    }

    public void DeleteRange(IEnumerable<MonitoredServer> entities)
    {
        deleteRepository.DeleteRange(entities);
    }
}
