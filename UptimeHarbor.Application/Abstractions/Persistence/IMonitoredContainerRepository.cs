using UptimeHarbor.Application.Abstractions.Persistence.Base;
using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Application.Abstractions.Persistence;

public interface IMonitoredContainerRepository :
    ICreateItemRepository<MonitoredContainer>,
    IDeleteItemRepository<MonitoredContainer>,
    IGetItemByIdRepository<MonitoredContainer, Guid>
{
    Task<List<MonitoredContainer>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task<List<MonitoredContainer>> GetEnabledAsync(
        CancellationToken cancellationToken = default);

    Task<List<MonitoredContainer>> GetByServerIdAsync(
        Guid serverId,
        CancellationToken cancellationToken = default);
}
