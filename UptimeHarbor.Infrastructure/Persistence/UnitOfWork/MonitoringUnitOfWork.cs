using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Monitoring.Models;

namespace UptimeHarbor.Infrastructure.Persistence.UnitOfWork;

public class MonitoringUnitOfWork(
    IUptimeHarborDbContextSession session,
    IMonitoredServerRepository monitoredServers,
    IMonitoredContainerRepository monitoredContainers,
    UptimeHarborDbContext dbContext)
    : UptimeHarborDbUnitOfWork(session), IMonitoringUnitOfWork
{
    public IMonitoredServerRepository MonitoredServers { get; } = monitoredServers;

    public IMonitoredContainerRepository MonitoredContainers { get; } = monitoredContainers;

    public void EnqueueNotification(MonitoringStateChangedEvent monitoringEvent)
    {
        dbContext.NotificationOutboxJobs.Add(NotificationOutboxJob.Create(monitoringEvent));
    }
}
