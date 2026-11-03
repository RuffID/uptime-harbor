using UptimeHarbor.Application.Installation;

namespace UptimeHarbor.Infrastructure.Installation;

public class UnavailableManagedRuntimeAdmission : IManagedRuntimeAdmission
{
    public Task<bool> CanStartAsync(LocalInstallationProfile profile, CancellationToken token)
    {
        return Task.FromResult(false);
    }

    public Task<bool> CanWorkAsync(LocalInstallationProfile profile, CancellationToken token)
    {
        return Task.FromResult(false);
    }
}
