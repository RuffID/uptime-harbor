using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Процедура controlled enable/fencing этапа 09 ещё не подключена.</summary>
public class UnavailableRecoveryAdmission : IRecoveryAdmission
{
    public ValueTask<RecoveryAdmission?> ReadAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<RecoveryAdmission?>(null);
    }
}
