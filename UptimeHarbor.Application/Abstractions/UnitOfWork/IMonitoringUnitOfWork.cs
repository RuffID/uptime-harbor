using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Monitoring.Models;

namespace UptimeHarbor.Application.Abstractions.UnitOfWork;

public interface IMonitoringUnitOfWork : IUnitOfWorkScope
{
    IMonitoredServerRepository MonitoredServers { get; }

    IMonitoredContainerRepository MonitoredContainers { get; }

    void EnqueueNotification(MonitoringStateChangedEvent monitoringEvent);
}
