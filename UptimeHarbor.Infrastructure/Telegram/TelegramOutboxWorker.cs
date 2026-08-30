using Microsoft.Extensions.Hosting;

namespace UptimeHarbor.Infrastructure.Telegram;

public class TelegramOutboxWorker(
    TelegramOutboxDispatcher dispatcher,
    TimeProvider timeProvider)
    : BackgroundService
{
    public static readonly TimeSpan POLL_INTERVAL = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await dispatcher.DispatchDueAsync(stoppingToken);
            await Task.Delay(POLL_INTERVAL, timeProvider, stoppingToken);
        }
    }
}
