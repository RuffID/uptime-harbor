using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Monitoring.Models;
using UptimeHarbor.Application.Notifications;
using UptimeHarbor.Application.Notifications.Models;

namespace UptimeHarbor.Infrastructure.Telegram;

public class TelegramMonitoringEventHandler(
    INotificationDeliveryService deliveryService,
    INotificationSender sender,
    IServiceScopeFactory scopeFactory,
    ILogger<TelegramMonitoringEventHandler> logger)
    : IMonitoringEventHandler
{
    public async Task HandleAsync(
        MonitoringStateChangedEvent monitoringEvent,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PendingNotificationDeliveryDto> deliveries;
        try
        {
            deliveries = await deliveryService.GetPendingDeliveriesAsync(
                monitoringEvent,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            logger.LogWarning(
                "Не удалось подготовить Telegram-уведомления для объекта {ObjectId}.",
                monitoringEvent.ObjectId);
            return;
        }

        foreach (PendingNotificationDeliveryDto delivery in deliveries)
        {
            try
            {
                await sender.SendAsync(delivery.Recipient, delivery.Message, cancellationToken);
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                INotificationDeliveryService scopedDeliveryService = scope.ServiceProvider
                    .GetRequiredService<INotificationDeliveryService>();
                ServiceResult result = await scopedDeliveryService.MarkDeliverySuccessfulAsync(
                    delivery.BindingId,
                    delivery.IncidentId,
                    delivery.Kind,
                    cancellationToken);
                if (!result.Success)
                {
                    logger.LogWarning(
                        "Не удалось зафиксировать Telegram-доставку для привязки {BindingId}.",
                        delivery.BindingId);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                logger.LogWarning(
                    "Ошибка Telegram-доставки для привязки {BindingId}, чата {ChatId} и типа {DeliveryKind}.",
                    delivery.BindingId,
                    delivery.Recipient.Id,
                    delivery.Kind);
            }
        }
    }
}
