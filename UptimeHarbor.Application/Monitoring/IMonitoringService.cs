using UptimeHarbor.Application.Monitoring.Models;

namespace UptimeHarbor.Application.Monitoring;

public interface IMonitoringService
{
    Task<IReadOnlyList<MonitoringTargetDto>> GetEnabledTargetsAsync(
        CancellationToken cancellationToken = default);

    Task<bool> RecordServerResultAsync(
        Guid serverId,
        string checkedEndpointUrl,
        ServerEndpointCheckResult result,
        int failuresBeforeUnavailable,
        CancellationToken cancellationToken = default);

    Task<bool> RecordContainerResultAsync(
        Guid containerId,
        string checkedEndpointUrl,
        ContainerEndpointCheckResult result,
        int failuresBeforeUnavailable,
        CancellationToken cancellationToken = default);
}
