using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Web.Cluster;

public class ClusterSnapshotWorker(RevisionClusterRecovery recovery, RevisionStateStore state,
    RevisionSnapshotStore snapshots, IOptions<ClusterOptions> options,
    TimeProvider timeProvider, ILogger<ClusterSnapshotWorker> logger) : UptimeHarbor.Infrastructure.Installation.RestartableWorker
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(options.Value.SnapshotIntervalSeconds), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            long lastIndex = snapshots.Current?.Manifest.Index ?? 0;
            if (!recovery.Accepted.CaughtUp || state.Current is null || state.Current.Index <= lastIndex)
            {
                continue;
            }

            try
            {
                await recovery.CreateSnapshotAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
            {
                logger.LogWarning("Cluster snapshot publication is unavailable for {NodeId}.", options.Value.NodeId);
            }
        }
    }
}
