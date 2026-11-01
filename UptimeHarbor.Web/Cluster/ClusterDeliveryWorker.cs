using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Web.Cluster;

/// <summary>Renewal не ждёт Telegram: независимый цикл сохраняет срок authority во время in-flight.</summary>
public class ClusterDeliveryWorker(IOptions<ClusterOptions> options, SenderAuthorityGate gate,
    AcceptedDeliveryDispatcher dispatcher, TimeProvider time, ILogger<ClusterDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1), time);
        Task dispatch = Task.CompletedTask;
        long? renewedAt = null;
        try
        {
            do
            {
                gate.Tick();
                if (!renewedAt.HasValue || time.GetElapsedTime(renewedAt.Value) >= TimeSpan.FromSeconds(5))
                {
                    try
                    {
                        if (dispatcher.ReconcileQueue())
                        {
                            await gate.RenewAsync(stoppingToken);
                        }
                        else
                        {
                            gate.Close();
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        gate.Close();
                        logger.LogWarning("Sender authority renewal deferred: {Category}.", "AuthorityUnavailable");
                    }

                    renewedAt = time.GetTimestamp();
                }

                if (dispatch.IsCompleted)
                {
                    await ObserveDispatchAsync(dispatch, stoppingToken);
                    dispatch = dispatcher.RunOnceAsync(stoppingToken);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        finally
        {
            gate.Close();
            await ObserveDispatchAsync(dispatch, stoppingToken);
        }
    }

    private async Task ObserveDispatchAsync(Task dispatch, CancellationToken token)
    {
        try
        {
            await dispatch;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Остановка закрывает gate; захваченные результаты остаются долговременными.
        }
        catch (Exception)
        {
            gate.Close();
            logger.LogWarning("Delivery reconciliation deferred: {Category}.", "HistoryOrJournalUnavailable");
        }
    }
}
