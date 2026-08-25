using UptimeHarbor.Application.Monitoring;

namespace UptimeHarbor.Application.Visibility;

public interface IMonitoringEventRecipientResolver
{
    Task<IReadOnlyList<Guid>> GetRecipientUserIdsAsync(
        MonitoringObjectKind objectKind,
        Guid objectId,
        Guid? serverId,
        CancellationToken cancellationToken = default);
}
