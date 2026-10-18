using EFCoreLibrary.Abstractions.Database.Repository.Base;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Infrastructure.Persistence.Repositories;

public class MonitoredServerRepository(
    IContextGetItemByPredicateRepository<MonitoredServer, UptimeHarborDbContext> getRepository,
    IContextCreateItemRepository<MonitoredServer, UptimeHarborDbContext> createRepository,
    IContextDeleteItemRepository<MonitoredServer, UptimeHarborDbContext> deleteRepository)
    : IMonitoredServerRepository
{
    public Task<MonitoredServer?> GetItemByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return getRepository.GetItemByPredicateAsync(
            server => server.Id == id,
            ct: cancellationToken);
    }

    public async Task<IReadOnlyList<MonitoredServer>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        List<MonitoredServer> servers = await getRepository.GetItemsByPredicateAsync(
            asNoTracking: true,
            ct: cancellationToken);
        return servers;
    }

    public async Task<IReadOnlyList<MonitoredServer>> GetEnabledAsync(
        CancellationToken cancellationToken = default)
    {
        List<MonitoredServer> servers = await getRepository.GetItemsByPredicateAsync(
            server => server.IsMonitoringEnabled,
            asNoTracking: true,
            ct: cancellationToken);
        return servers;
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
