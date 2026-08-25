using EFCoreLibrary.Abstractions.Database.Repository.Base;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Infrastructure.Persistence.Repositories;

public class MonitoredServerRepository(
    IGetItemByPredicateRepository<MonitoredServer, UptimeHarborDbContext> getRepository,
    ICreateItemRepository<MonitoredServer, UptimeHarborDbContext> createRepository,
    IDeleteItemRepository<MonitoredServer, UptimeHarborDbContext> deleteRepository)
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

    public Task<List<MonitoredServer>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        return getRepository.GetItemsByPredicateAsync(
            asNoTracking: true,
            ct: cancellationToken);
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
