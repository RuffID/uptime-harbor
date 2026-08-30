using UptimeHarbor.Application.Abstractions.Persistence.Base;
using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Application.Abstractions.Persistence;

public interface IMonitoredServerRepository :
    ICreateItemRepository<MonitoredServer>,
    IDeleteItemRepository<MonitoredServer>,
    IGetItemByIdRepository<MonitoredServer, Guid>
{
    Task<IReadOnlyList<MonitoredServer>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MonitoredServer>> GetEnabledAsync(
        CancellationToken cancellationToken = default);
}
