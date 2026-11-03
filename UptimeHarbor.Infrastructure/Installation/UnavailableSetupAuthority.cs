using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Installation;

namespace UptimeHarbor.Infrastructure.Installation;

public class UnavailableSetupAuthority : ISetupAuthority
{
    public Task<ServiceResult<SetupAuthorityEvidence>> PrepareBootstrapAsync(BootstrapPreparation preparation, CancellationToken token)
    {
        return Unavailable();
    }

    public Task<ServiceResult<SetupAuthorityEvidence>> PrepareJoinAsync(LocalOnboardingAttempt attempt, CancellationToken token)
    {
        return Unavailable();
    }

    public Task<bool> VerifyActivationAsync(LocalInstallationProfile profile, SetupAuthorityEvidence evidence, CancellationToken token)
    {
        return Task.FromResult(false);
    }

    private static Task<ServiceResult<SetupAuthorityEvidence>> Unavailable()
    {
        return Task.FromResult(ServiceResult<SetupAuthorityEvidence>.Fail(ServiceErrorType.Conflict,
            "Установление доверия ещё недоступно. Рабочий доступ закрыт."));
    }
}
