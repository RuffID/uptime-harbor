using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Web.Hubs;

[Authorize]
public class MonitoringHub(MonitoringConnections connections, IOptions<ClusterOptions> options) : Hub
{
    public override async Task OnConnectedAsync()
    {
        if (options.Value.Enabled && !connections.Register(Context))
        {
            return;
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
