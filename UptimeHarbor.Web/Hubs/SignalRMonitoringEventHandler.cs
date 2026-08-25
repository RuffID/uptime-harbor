using Microsoft.AspNetCore.SignalR;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Monitoring.Models;
using UptimeHarbor.Application.Visibility;
using UptimeHarbor.Web.Hubs.Models;

namespace UptimeHarbor.Web.Hubs;

public class SignalRMonitoringEventHandler(
    IMonitoringEventRecipientResolver recipientResolver,
    IHubContext<MonitoringHub> hubContext,
    ILogger<SignalRMonitoringEventHandler> logger)
    : IMonitoringEventHandler
{
    public async Task HandleAsync(
        MonitoringStateChangedEvent monitoringEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(monitoringEvent);

        try
        {
            IReadOnlyList<Guid> recipientIds =
                await recipientResolver.GetRecipientUserIdsAsync(
                    monitoringEvent.ObjectKind,
                    monitoringEvent.ObjectId,
                    monitoringEvent.ServerId,
                    cancellationToken);
            if (recipientIds.Count == 0)
            {
                return;
            }

            IReadOnlyList<string> signalRUserIds = recipientIds
                .Select(userId => userId.ToString())
                .ToList();
            MonitoringObjectChangedMessage message = new(
                monitoringEvent.ObjectId,
                monitoringEvent.ObjectKind);
            await hubContext.Clients.Users(signalRUserIds).SendAsync(
                "MonitoringObjectChanged",
                message,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Не удалось отправить SignalR-событие объекта {ObjectKind} {ObjectId}.",
                monitoringEvent.ObjectKind,
                monitoringEvent.ObjectId);
        }
    }
}
