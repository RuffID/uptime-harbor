using UptimeHarbor.Application.Monitoring.Models;

namespace UptimeHarbor.Application.Monitoring;

public class MonitoringEventPublisher(IEnumerable<IMonitoringEventHandler> handlers)
    : IMonitoringEventPublisher
{
    public Task PublishAsync(
        MonitoringStateChangedEvent monitoringEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(monitoringEvent);
        Task[] pendingHandlers = handlers
            .Select(handler => handler.HandleAsync(monitoringEvent, cancellationToken))
            .ToArray();
        return Task.WhenAll(pendingHandlers);
    }
}
