using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Installation;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Локальный genesis и проверка принятого Enable. Join не обходит отдельный enrollment-протокол.</summary>
public class SetupAuthority(GenesisPreparation genesis, OnlineEnableState enable, JoinEnrollmentAuthority? join = null) : ISetupAuthority
{
    public async Task<ServiceResult<SetupAuthorityEvidence>> PrepareBootstrapAsync(BootstrapPreparation preparation,
        CancellationToken token)
    {
        SetupAuthorityEvidence evidence = await genesis.PrepareAsync(preparation, token);
        return ServiceResult<SetupAuthorityEvidence>.Ok(evidence);
    }

    public async Task<ServiceResult<SetupAuthorityEvidence>> PrepareJoinAsync(LocalOnboardingAttempt attempt, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (join is null)
        {
            return ServiceResult<SetupAuthorityEvidence>.Fail(ServiceErrorType.Conflict, "EnrollmentProtocolRequired");
        }

        try
        {
            return await join.PrepareAsync(attempt, token);
        }
        catch (Exception error) when (error is InvalidDataException or UnauthorizedAccessException or FormatException
            or System.Security.Cryptography.CryptographicException)
        {
            return ServiceResult<SetupAuthorityEvidence>.Fail(ServiceErrorType.Conflict, "EnrollmentTrustRejected");
        }
    }

    public Task<bool> VerifyActivationAsync(LocalInstallationProfile profile, SetupAuthorityEvidence evidence, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        OnlineGrant? grant = enable.Current;
        return Task.FromResult(grant is not null && profile.AttemptId == evidence.AttemptId
            && profile.RuntimeGeneration == evidence.RuntimeGeneration
            && evidence.Kind == SetupAuthorityEvidenceKind.ActivationVerified
            && evidence.AcceptedIndex == enable.AcceptedIndex && evidence.Digest == grant.RequestDigest);
    }
}
