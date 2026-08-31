using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Notifications;
using UptimeHarbor.Application.Notifications.Models;

namespace UptimeHarbor.Infrastructure.Telegram;

public class TelegramOutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<TelegramOutboxDispatcher> logger)
{
    public const int MAXIMUM_ATTEMPTS = 5;
    public const int MAXIMUM_PARALLEL_JOBS = 4;
    public const int MAXIMUM_PARALLEL_RECIPIENTS = 4;
    public const int BATCH_SIZE = 20;
    public static readonly TimeSpan LEASE_DURATION = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan[] RETRY_DELAYS =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5)
    ];

    public async Task<int> DispatchDueAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset nowUtc = timeProvider.GetUtcNow();
        IReadOnlyList<ClaimedNotificationOutboxJob> jobs;
        await using (AsyncServiceScope scope = scopeFactory.CreateAsyncScope())
        {
            NotificationOutboxStore store =
                scope.ServiceProvider.GetRequiredService<NotificationOutboxStore>();
            jobs = await store.ClaimDueAsync(
                nowUtc,
                BATCH_SIZE,
                LEASE_DURATION,
                cancellationToken);
        }

        await Parallel.ForEachAsync(
            jobs,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MAXIMUM_PARALLEL_JOBS
            },
            ProcessJobAsync);
        return jobs.Count;
    }

    private async ValueTask ProcessJobAsync(
        ClaimedNotificationOutboxJob job,
        CancellationToken cancellationToken)
    {
        await using AsyncServiceScope processingScope = scopeFactory.CreateAsyncScope();
        NotificationOutboxStore store = processingScope.ServiceProvider
            .GetRequiredService<NotificationOutboxStore>();
        await store.ProcessClaimedAsync(
            job.Id,
            LEASE_DURATION,
            MAXIMUM_ATTEMPTS,
            GetRetryDelay(job.AttemptCount),
            processCancellationToken => ProcessDeliveriesAsync(job, processCancellationToken),
            cancellationToken);
    }

    private async Task<bool> ProcessDeliveriesAsync(
        ClaimedNotificationOutboxJob job,
        CancellationToken cancellationToken)
    {
        bool succeeded;
        try
        {
            IReadOnlyList<PendingNotificationDeliveryDto> deliveries;
            await using (AsyncServiceScope scope = scopeFactory.CreateAsyncScope())
            {
                INotificationDeliveryService deliveryService = scope.ServiceProvider
                    .GetRequiredService<INotificationDeliveryService>();
                deliveries = await deliveryService.GetPendingDeliveriesAsync(
                    job.MonitoringEvent,
                    cancellationToken);
            }

            ConcurrentBag<bool> results = [];
            await Parallel.ForEachAsync(
                deliveries,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = MAXIMUM_PARALLEL_RECIPIENTS
                },
                async (delivery, deliveryCancellationToken) =>
                {
                    results.Add(await DeliverAsync(delivery, deliveryCancellationToken));
                });
            succeeded = results.All(result => result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Не удалось обработать Telegram outbox job {OutboxJobId}.",
                job.Id);
            succeeded = false;
        }
        return succeeded;
    }

    private async Task<bool> DeliverAsync(
        PendingNotificationDeliveryDto delivery,
        CancellationToken cancellationToken)
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            INotificationSender sender = scope.ServiceProvider.GetRequiredService<INotificationSender>();
            await sender.SendAsync(delivery.Recipient, delivery.Message, cancellationToken);
            INotificationDeliveryService deliveryService = scope.ServiceProvider
                .GetRequiredService<INotificationDeliveryService>();
            ServiceResult result = await deliveryService.MarkDeliverySuccessfulAsync(
                delivery.BindingId,
                delivery.IncidentId,
                delivery.Kind,
                cancellationToken);
            return result.Success;
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
            return false;
        }
    }

    private static TimeSpan GetRetryDelay(int attemptCount)
    {
        int index = Math.Clamp(attemptCount - 1, 0, RETRY_DELAYS.Length - 1);
        return RETRY_DELAYS[index];
    }
}
