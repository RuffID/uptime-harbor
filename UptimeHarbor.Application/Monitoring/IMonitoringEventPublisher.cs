using UptimeHarbor.Application.Monitoring.Models;

namespace UptimeHarbor.Application.Monitoring;

public interface IMonitoringEventPublisher
{
    Task PublishAsync(
        MonitoringStateChangedEvent monitoringEvent,
        CancellationToken cancellationToken = default);
}
