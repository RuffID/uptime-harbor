using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster;

public interface IClusterLocalInitialization
{
    Task InitializeAsync(RecoveryAdmission admission, CancellationToken cancellationToken);
}
