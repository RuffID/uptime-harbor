using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Выбор одного основания по intent; online отказ не переключается на offline fallback.</summary>
public class ManagedRecoveryAdmission(IOptions<InstallationOptions> installation, ManagedAdmissionState managed,
    SignedRecoveryAdmission offline) : IRecoveryAdmission
{
    public ValueTask<RecoveryAdmission?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (installation.Value.Setup)
        {
            return ValueTask.FromResult(managed.Read());
        }

        return offline.ReadAsync(cancellationToken);
    }
}
