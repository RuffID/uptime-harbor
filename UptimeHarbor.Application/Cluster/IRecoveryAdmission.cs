using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster;

public interface IRecoveryAdmission
{
    ValueTask<RecoveryAdmission?> ReadAsync(CancellationToken cancellationToken);
}
