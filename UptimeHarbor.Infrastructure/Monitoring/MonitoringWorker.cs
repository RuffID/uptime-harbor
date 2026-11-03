using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Monitoring;

namespace UptimeHarbor.Infrastructure.Monitoring;

public class MonitoringWorker(
    IMonitoringCycleRunner cycleRunner,
    IOptions<MonitoringOptions> options,
    TimeProvider timeProvider)
    : UptimeHarbor.Infrastructure.Installation.RestartableWorker
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan interval = TimeSpan.FromSeconds(options.Value.IntervalSeconds);
        using PeriodicTimer timer = new(interval, timeProvider);

        do
        {
            await cycleRunner.RunCycleAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
