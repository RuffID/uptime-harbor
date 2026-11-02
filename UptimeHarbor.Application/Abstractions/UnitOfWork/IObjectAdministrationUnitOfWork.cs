using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Monitoring;

namespace UptimeHarbor.Application.Abstractions.UnitOfWork;

public interface IObjectAdministrationUnitOfWork : IUnitOfWorkScope
{
    IMonitoredServerRepository MonitoredServers { get; }

    IMonitoredContainerRepository MonitoredContainers { get; }

    Task RemoveNotificationBindingsAsync(Guid targetId, CancellationToken cancellationToken = default);

    Task CancelPendingRecoveryNotificationsAsync(
        MonitoringObjectKind objectKind,
        Guid objectId,
        Guid incidentId,
        RecoveryNotificationCancellationReason reason,
        CancellationToken cancellationToken = default);
}
