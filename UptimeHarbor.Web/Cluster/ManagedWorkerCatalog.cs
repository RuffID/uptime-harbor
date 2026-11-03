using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Monitoring;
using UptimeHarbor.Infrastructure.Telegram;

namespace UptimeHarbor.Web.Cluster;

/// <summary>Полный deferred inventory. Resolve вызывается только после runtime admission.</summary>
public class ManagedWorkerCatalog(IServiceProvider services) : IManagedWorkerCatalog
{
    public IReadOnlyList<RestartableWorker> Resolve(bool cluster, bool quarantine, bool managedSetup)
    {
        if (!cluster)
        {
            return [services.GetRequiredService<MonitoringWorker>(), services.GetRequiredService<TelegramOutboxWorker>()];
        }

        List<RestartableWorker> workers =
        [
            services.GetRequiredService<ClusterHealthWorker>(), services.GetRequiredService<ClusterPeerRuntime>(),
            services.GetRequiredService<ClusterLifecycleWorker>(), services.GetRequiredService<ClusterRevisionWorker>(),
            services.GetRequiredService<ClusterSnapshotWorker>()
        ];
        if (!quarantine)
        {
            workers.Add(services.GetRequiredService<ClusterMonitoringWorker>());
            if (!managedSetup)
            {
                workers.Add(services.GetRequiredService<ClusterDeliveryWorker>());
            }
        }

        return workers;
    }
}
