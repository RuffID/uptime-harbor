using Microsoft.Extensions.Options;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Monitoring;

namespace UptimeHarbor.Web.Cluster;

public class ClusterMonitoringWorker(ClusterMonitoringCycle cycle, ObservationReplicationPump observations,
    MonitoringCoordinator coordinator, IOptions<ClusterOptions> options, TimeProvider time,
    ILogger<ClusterMonitoringWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await cycle.RunOnceAsync(stoppingToken);
            try
            {
                await observations.PumpAsync(stoppingToken);
                await coordinator.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                logger.LogDebug("Cluster monitoring reconciliation is unavailable for {NodeId}.", options.Value.NodeId);
            }
        }
    }
}
