using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Web.Cluster;

public interface IManagedWorkerCatalog
{
    IReadOnlyList<RestartableWorker> Resolve(bool cluster, bool quarantine, bool managedSetup);
}
