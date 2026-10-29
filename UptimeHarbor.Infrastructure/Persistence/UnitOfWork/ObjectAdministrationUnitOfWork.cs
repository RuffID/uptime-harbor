using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Monitoring.Models;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Domain.Access;

namespace UptimeHarbor.Infrastructure.Persistence.UnitOfWork;

public class ObjectAdministrationUnitOfWork(
    IUptimeHarborDbContextSession session,
    IMonitoredServerRepository monitoredServers,
    IMonitoredContainerRepository monitoredContainers,
    UptimeHarborDbContext dbContext,
    TimeProvider timeProvider)
    : UptimeHarborDbUnitOfWork(session), IObjectAdministrationUnitOfWork
{
    public IMonitoredServerRepository MonitoredServers { get; } = monitoredServers;

    public IMonitoredContainerRepository MonitoredContainers { get; } = monitoredContainers;

    public Task RemoveContainerPermissionsAsync(
        Guid containerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Ресурсных назначений в целевой схеме нет; метод сохранён для прежних вызывающих сценариев.
        return Task.CompletedTask;
    }

    public async Task CancelPendingRecoveryNotificationsAsync(
        MonitoringObjectKind objectKind,
        Guid objectId,
        Guid incidentId,
        RecoveryNotificationCancellationReason reason,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset nowUtc = timeProvider.GetUtcNow();
        List<NotificationOutboxJob> candidates = await dbContext.NotificationOutboxJobs
            .AsNoTracking()
            .Where(job => job.CompletedAtUtc == null && job.FailedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (NotificationOutboxJob candidate in candidates)
        {
            MonitoringStateChangedEvent monitoringEvent = candidate.ReadEvent();
            if (monitoringEvent.Transition != MonitoringTransition.Recovered
                || monitoringEvent.ObjectKind != objectKind
                || monitoringEvent.ObjectId != objectId
                || monitoringEvent.CurrentIncidentId != incidentId)
            {
                continue;
            }

            candidate.Cancel(nowUtc, reason);
            await dbContext.NotificationOutboxJobs
                .Where(job => job.Id == candidate.Id
                    && job.CompletedAtUtc == null
                    && job.FailedAtUtc == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(job => job.PayloadJson, candidate.PayloadJson)
                        .SetProperty(job => job.CompletedAtUtc, candidate.CompletedAtUtc)
                        .SetProperty(job => job.LockedUntilUtc, candidate.LockedUntilUtc),
                    cancellationToken);
        }
    }
}
