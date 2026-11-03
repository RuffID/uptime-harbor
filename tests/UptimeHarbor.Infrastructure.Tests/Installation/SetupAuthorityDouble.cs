using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Installation;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class SetupAuthorityDouble : ISetupAuthority
{
    public Func<BootstrapPreparation, ServiceResult<SetupAuthorityEvidence>>? Bootstrap { get; set; }
    public Func<LocalOnboardingAttempt, ServiceResult<SetupAuthorityEvidence>>? Join { get; set; }
    public BootstrapPreparation? Last { get; private set; }
    public bool Activation { get; set; }

    public Task<ServiceResult<SetupAuthorityEvidence>> PrepareBootstrapAsync(BootstrapPreparation request, CancellationToken token)
    {
        Last = request;
        return Task.FromResult((Bootstrap ?? throw new InvalidOperationException("UnexpectedBootstrap"))(request));
    }

    public Task<ServiceResult<SetupAuthorityEvidence>> PrepareJoinAsync(LocalOnboardingAttempt attempt, CancellationToken token)
    {
        return Task.FromResult((Join ?? throw new InvalidOperationException("UnexpectedJoin"))(attempt));
    }

    public Task<bool> VerifyActivationAsync(LocalInstallationProfile profile, SetupAuthorityEvidence evidence, CancellationToken token)
    {
        return Task.FromResult(Activation);
    }
}
