using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Persistence;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Сериализует durable фазы. Никакой SQL-транзакции вокруг runtime/network.</summary>
public class InstallationLifecycle(ILocalSetupUnitOfWork work, ILocalSetupAccountStore accounts,
    InstallationState state, IInstallationRuntime runtime, ISetupAuthority authority,
    InstallationLease lease, TimeProvider time, ILogger<InstallationLifecycle> logger)
{
    public async Task<ServiceResult<SetupStatus>> AdvanceAsync(long expectedRevision, long expectedGeneration,
        InstallationPhase nextPhase, SetupAuthorityEvidence? evidence, CancellationToken token)
    {
        await state.Transition.WaitAsync(token);
        try
        {
            LocalInstallationProfile current = await work.Profiles.ReadAsync(token)
                ?? throw new InvalidDataException("InstallationProfileMissing");
            if (current.ProfileRevision != expectedRevision || current.RuntimeGeneration != expectedGeneration
                || current.BootId != lease.BootId || !InstallationTransitions.Allowed(current.Phase, nextPhase))
            {
                return Reject("InstallationRevisionConflict");
            }

            if (nextPhase == InstallationPhase.ClusterReady)
            {
                bool exact = evidence is not null && evidence.AttemptId == current.AttemptId
                    && evidence.RuntimeGeneration == current.RuntimeGeneration && evidence.AcceptedIndex > 0
                    && evidence.Kind == SetupAuthorityEvidenceKind.ActivationVerified;
                if (!exact || !await authority.VerifyActivationAsync(current, evidence!, token))
                {
                    return Reject("OnlineActivationRequired");
                }
            }

            LocalInstallationProfile next = current with
            {
                Phase = nextPhase, ProfileRevision = current.ProfileRevision + 1,
                LeaseUntilUtc = time.GetUtcNow().AddMinutes(2)
            };
            if (nextPhase == InstallationPhase.Quiescing)
            {
                next = next with { RuntimeGeneration = current.RuntimeGeneration + 1 };
            }

            await work.ExecuteInTransactionAsync(async cancellation =>
            {
                if (nextPhase == InstallationPhase.ClusterReady)
                {
                    LocalSetupAccount account = await accounts.ReadAsync(cancellation)
                        ?? throw new InvalidDataException("SetupAccountMissing");
                    account.Disabled = true;
                    account.PasswordHash = null;
                    account.Stamp = Guid.NewGuid().ToString("N");
                    account.Revision++;
                    next = next with { SetupEpoch = current.SetupEpoch + 1 };
                }

                await work.Profiles.ReplaceAsync(next, current.ProfileRevision, cancellation);
            }, token);
            state.Publish(next);

            try
            {
                if (nextPhase == InstallationPhase.Quiescing)
                {
                    bool drained = await runtime.DrainAsync(current.RuntimeGeneration, TimeSpan.FromSeconds(10), token);
                    if (!drained)
                    {
                        return await FailAsync(next, "RuntimeDrainFailed");
                    }
                }

                if (nextPhase == InstallationPhase.ClusterQuarantine)
                {
                    await runtime.StartQuarantinedAsync(next, token);
                }

                if (nextPhase == InstallationPhase.ClusterReady)
                {
                    logger.LogInformation("{EventName} {InstallIdentity} {AttemptId} {Revision}",
                        "SetupAccessRevoked", next.InstallIdentity, next.AttemptId, next.ProfileRevision);
                    await runtime.PublishReadyAsync(next, token);
                    state.ConfirmCommonOpen(next);
                }
            }
            catch (Exception)
            {
                // Внешний эффект мог начаться. Не откатываем профиль и не повторяем запуск автоматически.
                return await FailAsync(next, "RuntimeEffectUnknown");
            }

            logger.LogInformation("{EventName} {AttemptId} {Phase} {Revision} {Generation}",
                "RuntimeTransitionCommitted", next.AttemptId, next.Phase, next.ProfileRevision, next.RuntimeGeneration);
            return ServiceResult<SetupStatus>.Ok(InstallationPreparation.Status(next, state.CommonOpen));
        }
        finally
        {
            state.Transition.Release();
        }
    }

    private async Task<ServiceResult<SetupStatus>> FailAsync(LocalInstallationProfile profile, string reason)
    {
        LocalInstallationProfile failed = profile with
        {
            Phase = InstallationPhase.FailedNeedsAction, ErrorCode = reason,
            ProfileRevision = profile.ProfileRevision + 1
        };
        await work.ExecuteInTransactionAsync(async cancellation =>
            await work.Profiles.ReplaceAsync(failed, profile.ProfileRevision, cancellation), CancellationToken.None);
        state.Publish(failed);
        logger.LogWarning("{EventName} {AttemptId} {Phase} {Revision} {Generation} {Reason}",
            "RuntimeTransitionFailed", failed.AttemptId, failed.Phase, failed.ProfileRevision,
            failed.RuntimeGeneration, reason);
        return Reject(reason);
    }

    private static ServiceResult<SetupStatus> Reject(string reason)
    {
        return ServiceResult<SetupStatus>.Fail(ServiceErrorType.Conflict, reason);
    }
}
