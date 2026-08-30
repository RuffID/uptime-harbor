using Microsoft.AspNetCore.SignalR;
using UptimeHarbor.Application.ObjectAdministration;

namespace UptimeHarbor.Web.Hubs;

public class SignalRObjectAdministrationChangePublisher(
    IHubContext<MonitoringHub> hubContext,
    ILogger<SignalRObjectAdministrationChangePublisher> logger)
    : IObjectAdministrationChangePublisher
{
    public async Task PublishObjectsChangedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await hubContext.Clients.All.SendAsync(
                "ObjectAdministrationChanged",
                cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Не удалось отправить SignalR-сигнал обновления административных данных.");
        }
    }
}
