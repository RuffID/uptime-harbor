using UptimeHarbor.Application.Monitoring.Models;

namespace UptimeHarbor.Application.Monitoring;

public interface IMonitoringEventHandler
{
    Task HandleAsync(
        MonitoringStateChangedEvent monitoringEvent,
        CancellationToken cancellationToken = default);
}
