using Microsoft.AspNetCore.SignalR;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Monitoring.Models;
using UptimeHarbor.Application.Visibility;
using UptimeHarbor.Web.Hubs.Models;
using Microsoft.Extensions.Options;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Web.Hubs;

public class SignalRMonitoringEventHandler(
    IMonitoringEventRecipientResolver recipientResolver,
    IHubContext<MonitoringHub> hubContext,
    ILogger<SignalRMonitoringEventHandler> logger,
    MonitoringConnections? connections = null,
    IOptions<ClusterOptions>? cluster = null)
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
            IClientProxy recipients = hubContext.Clients.Users(signalRUserIds);
            if (cluster?.Value.Enabled == true)
            {
                recipients = hubContext.Clients.Clients(connections!.AuthorizedIds(recipientIds));
            }

            await recipients.SendAsync(
                "MonitoringObjectChanged",
                message,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            logger.LogError(
                "SignalR monitoring refresh failed for {ObjectKind} {ObjectId}.",
                monitoringEvent.ObjectKind,
                monitoringEvent.ObjectId);
        }
    }
}
