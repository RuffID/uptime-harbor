using UptimeHarbor.Application.Abstractions.Persistence.Base;
using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Application.Abstractions.Persistence;

public interface IMonitoredContainerRepository :
    ICreateItemRepository<MonitoredContainer>,
    IDeleteItemRepository<MonitoredContainer>,
    IGetItemByIdRepository<MonitoredContainer, Guid>
{
    Task<IReadOnlyList<MonitoredContainer>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MonitoredContainer>> GetEnabledAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MonitoredContainer>> GetByServerIdAsync(
        Guid serverId,
        CancellationToken cancellationToken = default);
}
