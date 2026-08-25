using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;

namespace UptimeHarbor.Infrastructure.Persistence.UnitOfWork;

public class MonitoringUnitOfWork(
    IUptimeHarborDbContextSession session,
    IMonitoredServerRepository monitoredServers,
    IMonitoredContainerRepository monitoredContainers)
    : UptimeHarborDbUnitOfWork(session), IMonitoringUnitOfWork
{
    public IMonitoredServerRepository MonitoredServers { get; } = monitoredServers;

    public IMonitoredContainerRepository MonitoredContainers { get; } = monitoredContainers;
}
