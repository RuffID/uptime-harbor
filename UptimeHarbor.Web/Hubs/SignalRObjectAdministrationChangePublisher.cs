using Microsoft.AspNetCore.SignalR;
using UptimeHarbor.Application.ObjectAdministration;

namespace UptimeHarbor.Web.Hubs;

public class SignalRObjectAdministrationChangePublisher(IHubContext<MonitoringHub> hubContext)
    : IObjectAdministrationChangePublisher
{
    public Task PublishObjectsChangedAsync(CancellationToken cancellationToken = default)
    {
        return hubContext.Clients.All.SendAsync(
            "ObjectAdministrationChanged",
            cancellationToken: cancellationToken);
    }
}
