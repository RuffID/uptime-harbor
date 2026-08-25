using UptimeHarbor.Application.Abstractions.Persistence.Base;
using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Application.Abstractions.Persistence;

public interface IMonitoredServerRepository :
    ICreateItemRepository<MonitoredServer>,
    IDeleteItemRepository<MonitoredServer>,
    IGetItemByIdRepository<MonitoredServer, Guid>
{
    Task<List<MonitoredServer>> GetAllAsync(
        CancellationToken cancellationToken = default);
}
