using UptimeHarbor.Application.Monitoring.Models;

namespace UptimeHarbor.Application.Monitoring;

public interface IServerEndpointChecker
{
    Task<ServerEndpointCheckResult> CheckAsync(
        string endpointUrl,
        CancellationToken cancellationToken = default);
}
