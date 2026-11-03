using System.Text.Json;
using Microsoft.Extensions.Logging;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Installation;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Сохраняет исходный authority request до вызова порта. Не публикует Ready.</summary>
public class SetupAuthorityCoordinator(ILocalSetupUnitOfWork work, InstallationState state,
    ISetupAuthority authority, OnboardingProgress progress, InstallationLease lease,
    TimeProvider time, ILogger<SetupAuthorityCoordinator> logger)
{
    public async Task<ServiceResult<SetupStatus>> PrepareBootstrapAsync(CancellationToken token)
    {
        await state.Transition.WaitAsync(token);
        try
        {
            LocalInstallationProfile profile = await ProfileAsync("Bootstrap", token);
            if (profile.BootstrapAccountDigest is null || profile.BootstrapAccountReference is null || profile.ClusterId is null)
            {
                throw new InvalidDataException("BootstrapPreparationIncomplete");
            }

            LocalAttemptStep step = await SaveIntentAsync(profile, "bootstrap", "GenesisPrepared", profile.BootstrapAccountDigest, token);
            BootstrapPreparation request = new(profile.InstallIdentity, profile.AttemptId!.Value, profile.ClusterId.Value,
                profile.NodeId!.Value, profile.StorageGeneration!.Value, profile.PeerAddress!, profile.BootstrapAccountDigest,
                profile.BootstrapAccountReference, profile.BotId, step.ProfileRevision, step.RuntimeGeneration);
            ServiceResult<SetupAuthorityEvidence> result = await InvokeAsync(
                cancellation => authority.PrepareBootstrapAsync(request, cancellation), token);
            return await RecordAsync(profile, result, CancellationToken.None);
        }
        finally
        {
            state.Transition.Release();
        }
    }

    public async Task<ServiceResult<SetupStatus>> PrepareJoinAsync(LocalOnboardingAttempt intent, CancellationToken token)
    {
        await state.Transition.WaitAsync(token);
        try
        {
            LocalInstallationProfile profile = await ProfileAsync("Join", token);
            bool same = intent.AttemptId == profile.AttemptId && intent.Request.NodeId == profile.NodeId
                && intent.Request.StorageGeneration == profile.StorageGeneration && intent.Request.PeerUri == profile.PeerAddress
                && intent.PrivateKeyReference == profile.KeyReference;
            if (!same)
            {
                return ServiceResult<SetupStatus>.Fail(ServiceErrorType.Conflict, "AttemptIdentityMismatch");
            }

            LocalOnboardingAttempt saved = await progress.PrepareAsync(intent, token);
            await SaveIntentAsync(profile, "reserve", "ReservationAccepted", saved.RequestDigest, token);
            ServiceResult<SetupAuthorityEvidence> result = await InvokeAsync(
                cancellation => authority.PrepareJoinAsync(saved, cancellation), token);
            if (result.Success)
            {
                SetupAuthorityEvidence evidence = result.Data!;
                bool reserved = evidence.AttemptId == profile.AttemptId && evidence.RuntimeGeneration == profile.RuntimeGeneration
                    && evidence.Kind == SetupAuthorityEvidenceKind.ReservationAccepted && evidence.AcceptedIndex > 0
                    && evidence.Digest == saved.RequestDigest;
                if (!reserved)
                {
                    return await RecordAsync(profile, ServiceResult<SetupAuthorityEvidence>.Fail(
                        ServiceErrorType.Conflict, "AuthorityEvidenceInvalid"), CancellationToken.None);
                }

                await progress.ReservedAsync(saved.AttemptId, evidence.AcceptedIndex, saved.Revision, CancellationToken.None);
            }

            return await RecordAsync(profile, result, CancellationToken.None);
        }
        finally
        {
            state.Transition.Release();
        }
    }

    private async Task<LocalInstallationProfile> ProfileAsync(string mode, CancellationToken token)
    {
        LocalInstallationProfile profile = await work.Profiles.ReadAsync(token)
            ?? throw new InvalidDataException("InstallationProfileMissing");
        if (profile.BootId != lease.BootId || profile.Mode != mode || profile.Phase != InstallationPhase.LocalPrepared
            || profile.AttemptId is null || profile.NodeId is null || profile.StorageGeneration is null)
        {
            throw new InvalidOperationException("AuthorityPreparationPhaseInvalid");
        }

        return profile;
    }

    private async Task<LocalAttemptStep> SaveIntentAsync(LocalInstallationProfile profile, string operation, string expected, string digest, CancellationToken token)
    {
        Guid id = profile.OperationIds![operation];
        LocalAttemptStep? step = await work.Profiles.FindStepAsync(id, token);
        if (step is not null)
        {
            if (step.AttemptId != profile.AttemptId || step.RequestDigest != digest || step.ExpectedResult != expected)
            {
                throw new InvalidDataException("AuthorityOperationPayloadChanged");
            }

            return step;
        }

        LocalAttemptStep prepared = new(id, profile.AttemptId!.Value, operation, digest, expected,
            profile.ProfileRevision, profile.RuntimeGeneration, time.GetUtcNow());
        work.Profiles.AddStep(prepared);
        await work.SaveChangesAsync(token);
        return prepared;
    }

    private async Task<ServiceResult<SetupStatus>> RecordAsync(LocalInstallationProfile profile,
        ServiceResult<SetupAuthorityEvidence> result, CancellationToken token)
    {
        LocalInstallationProfile next = profile with { ProfileRevision = profile.ProfileRevision + 1 };
        if (result.Success)
        {
            SetupAuthorityEvidence evidence = result.Data!;
            bool exact = evidence.AttemptId == profile.AttemptId && evidence.RuntimeGeneration == profile.RuntimeGeneration;
            if (profile.Mode == "Bootstrap")
            {
                exact = exact && evidence.Kind == SetupAuthorityEvidenceKind.GenesisPrepared
                    && evidence.AcceptedIndex > 0 && evidence.Digest == profile.BootstrapAccountDigest;
            }

            if (!exact)
            {
                return await RecordAsync(profile, ServiceResult<SetupAuthorityEvidence>.Fail(
                    ServiceErrorType.Conflict, "AuthorityEvidenceInvalid"), token);
            }

            next = next with { Evidence = JsonSerializer.Serialize(evidence), ErrorCode = null };
        }
        else
        {
            string reason = "AuthorityUnavailable";
            if (result.Error!.Message is "AuthorityResultUnknown" or "AuthorityEvidenceInvalid")
            {
                reason = result.Error.Message;
            }

            next = next with { ErrorCode = reason };
        }

        await work.ExecuteInTransactionAsync(async cancellation =>
            await work.Profiles.ReplaceAsync(next, profile.ProfileRevision, cancellation), token);
        state.Publish(next);
        if (!result.Success)
        {
            logger.LogWarning("{EventName} {AttemptId} {Revision} {Generation} {Reason}",
                "SetupAuthorityDeferred", next.AttemptId, next.ProfileRevision, next.RuntimeGeneration, next.ErrorCode);
        }
        else
        {
            logger.LogInformation("{EventName} {AttemptId} {Revision} {Generation} {EvidenceKind}",
                "SetupAuthorityEvidenceRecorded", next.AttemptId, next.ProfileRevision, next.RuntimeGeneration, result.Data!.Kind);
        }

        return ServiceResult<SetupStatus>.Ok(InstallationPreparation.Status(next));
    }

    private static async Task<ServiceResult<SetupAuthorityEvidence>> InvokeAsync(
        Func<CancellationToken, Task<ServiceResult<SetupAuthorityEvidence>>> request, CancellationToken token)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            return await request(timeout.Token).WaitAsync(timeout.Token);
        }
        catch (HttpRequestException)
        {
            return ServiceResult<SetupAuthorityEvidence>.Fail(ServiceErrorType.Conflict, "AuthorityResultUnknown");
        }
        catch (OperationCanceledException)
        {
            return ServiceResult<SetupAuthorityEvidence>.Fail(ServiceErrorType.Conflict, "AuthorityResultUnknown");
        }
    }
}
