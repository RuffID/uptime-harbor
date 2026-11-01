using Microsoft.AspNetCore.SignalR;
using UptimeHarbor.Application.ObjectAdministration;
using Microsoft.Extensions.Options;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Web.Hubs;

public class SignalRObjectAdministrationChangePublisher(
    IHubContext<MonitoringHub> hubContext,
    ILogger<SignalRObjectAdministrationChangePublisher> logger,
    MonitoringConnections? connections = null,
    IOptions<ClusterOptions>? cluster = null)
    : IObjectAdministrationChangePublisher
{
    public async Task PublishObjectsChangedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IClientProxy recipients = hubContext.Clients.All;
            if (cluster?.Value.Enabled == true)
            {
                recipients = hubContext.Clients.Clients(connections!.AuthorizedIds());
            }

            await recipients.SendAsync(
                "ObjectAdministrationChanged",
                cancellationToken: cancellationToken);
        }
        catch
        {
            logger.LogError(
                "SignalR administration refresh failed.");
        }
    }
}
