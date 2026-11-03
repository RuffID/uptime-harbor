using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;

namespace UptimeHarbor.Application.Installation;

/// <summary>Типизированная граница будущего genesis/issuer; не универсальный signer.</summary>
public interface ISetupAuthority
{
    Task<ServiceResult<SetupAuthorityEvidence>> PrepareBootstrapAsync(
        BootstrapPreparation preparation, CancellationToken token);
    Task<ServiceResult<SetupAuthorityEvidence>> PrepareJoinAsync(
        LocalOnboardingAttempt attempt, CancellationToken token);
    Task<bool> VerifyActivationAsync(LocalInstallationProfile profile,
        SetupAuthorityEvidence evidence, CancellationToken token);
}
