using UptimeHarbor.Application.Monitoring.Models;

namespace UptimeHarbor.Application.Monitoring;

public interface IContainerEndpointChecker
{
    Task<ContainerEndpointCheckResult> CheckAsync(
        string endpointUrl,
        CancellationToken cancellationToken = default);
}
