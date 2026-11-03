using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Identity;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Выдача и управление приглашением через прежние protected Pending и transport.</summary>
public class InvitationAdministration(ClusterAdministrationAccess access, RevisionStateStore state,
    PendingCommandService pending, IPendingControlUnitOfWork work, IRevisionPayloadProtection protection,
    IRevisionTransport transport, ILocalAccountLogin login, IUserAccessContext user,
    IOptions<ClusterOptions> configured, TimeProvider time, IOnboardingAudit? audit = null)
{
    private long? verifiedActorRevision;
    public async Task<ServiceResult<InvitationResponse>> ReadAsync(Guid invitationId, Guid operationId, CancellationToken token)
    {
        ServiceError? error = AccessError();
        if (error is not null)
        {
            return ServiceResult<InvitationResponse>.Fail(error);
        }

        PendingOperation? local = await work.PendingOperations.GetAsync(operationId, token);
        if (AccessError() is ServiceError changed)
        {
            return ServiceResult<InvitationResponse>.Fail(changed);
        }

        return ServiceResult<InvitationResponse>.Ok(Response(invitationId, operationId, local is not null));
    }

    public async Task<ServiceResult<InvitationResponse>> IssueAsync(Guid operationId, Guid? replacesId, string password, CancellationToken token)
    {
        ServiceError? error = await ReauthenticateAsync(password, token);
        if (error is not null)
        {
            return Denied(operationId, operationId, error);
        }

        try
        {
            StorageGuards.Id(operationId, nameof(operationId));
            RevisionImage image = state.Current!;
            if (image.Results.ContainsKey(operationId))
            {
                string acceptedAction = "IssueInvitation";
                if (replacesId.HasValue)
                {
                    acceptedAction = "ReplaceInvitation";
                }

                if (!MatchesAccepted(image, operationId, operationId, Guid.Empty, acceptedAction, replacesId))
                {
                    return Conflict();
                }

                return ServiceResult<InvitationResponse>.Ok(Response(operationId, operationId, false));
            }

            PendingOperation? prior = await work.PendingOperations.GetAsync(operationId, token);
            if (AccessError() is ServiceError changed)
            {
                return ServiceResult<InvitationResponse>.Fail(changed);
            }

            string action = "IssueInvitation";
            if (replacesId.HasValue)
            {
                StorageGuards.Id(replacesId.Value, nameof(replacesId));
                action = "ReplaceInvitation";
            }

            if (prior is not null)
            {
                RevisionCommand original = RevisionCodec.Decode(protection.Unprotect(prior.ProtectedPayload));
                bool same = original.Action == action && original.EntityId == operationId
                    && original.Onboarding?.ReplacesInvitationId == replacesId && original.ActorUserId == user.UserId;
                if (!same)
                {
                    return Conflict();
                }

                await SubmitAsync(original, token);
                return await ReadAsync(operationId, operationId, token);
            }

            Guid code = InvitationSecrets.CreateCode();
            image = state.Current!;
            string verifier = InvitationSecrets.Verifier(image.ClusterId, image.Generation, operationId, code);
            RevisionCommand command = Prepare(action, operationId, operationId,
                new OnboardingCommand
                {
                    StorageGeneration = configured.Value.StorageGeneration, InvitationId = operationId,
                    ReplacesInvitationId = replacesId, Verifier = verifier, ReauthenticatedAtUtc = time.GetUtcNow()
                });
            bool acknowledged = await SubmitAsync(command, token);
            ServiceResult<InvitationResponse> response = await ReadAsync(operationId, operationId, token);
            bool currentActor = state.Find(user.UserId)?.Revision == command.ActorRevision;
            if (response.Success && acknowledged && currentActor && response.Data!.OperationStatus == "Committed")
            {
                return ServiceResult<InvitationResponse>.Ok(response.Data with { Code = code.ToString("D") });
            }

            return response;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return Invalid();
        }
        catch (UnauthorizedAccessException)
        {
            return ServiceResult<InvitationResponse>.Fail(ServiceErrorType.Forbidden, "Требуется активный принятый администратор.");
        }
        catch (InvalidOperationException exception) when (exception.Message is "IdempotencyKeyReuse" or "PendingQueueFull"
            or "PendingRequiresReconciliation")
        {
            return Conflict();
        }
    }

    public async Task<ServiceResult<InvitationResponse>> MutateAsync(string action, Guid invitationId,
        Guid attemptId, Guid operationId, string password, CancellationToken token)
    {
        ServiceError? error = await ReauthenticateAsync(password, token);
        if (error is not null)
        {
            return Denied(operationId, invitationId, error);
        }

        if (action is not ("RevokeInvitation" or "ContinueOnboardingAttempt" or "CancelOnboardingAttempt"))
        {
            return Invalid();
        }

        try
        {
            StorageGuards.Id(operationId, nameof(operationId));
            StorageGuards.Id(invitationId, nameof(invitationId));
            RevisionImage image = state.Current!;
            if (image.Results.ContainsKey(operationId))
            {
                if (!MatchesAccepted(image, operationId, invitationId, attemptId, action, null))
                {
                    return Conflict();
                }

                return await ReadAsync(invitationId, operationId, token);
            }

            PendingOperation? prior = await work.PendingOperations.GetAsync(operationId, token);
            if (AccessError() is ServiceError changed)
            {
                return ServiceResult<InvitationResponse>.Fail(changed);
            }

            RevisionCommand command;
            if (prior is not null)
            {
                command = RevisionCodec.Decode(protection.Unprotect(prior.ProtectedPayload));
                if (command.Action != action || command.EntityId != invitationId || command.Onboarding?.AttemptId != attemptId
                    || command.ActorUserId != user.UserId)
                {
                    return Conflict();
                }
            }
            else
            {
                command = Prepare(action, operationId, invitationId, new OnboardingCommand
                {
                    StorageGeneration = configured.Value.StorageGeneration, InvitationId = invitationId,
                    AttemptId = attemptId, ReauthenticatedAtUtc = time.GetUtcNow()
                });
            }

            await SubmitAsync(command, token);
            return await ReadAsync(invitationId, operationId, token);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return Invalid();
        }
        catch (UnauthorizedAccessException)
        {
            return ServiceResult<InvitationResponse>.Fail(ServiceErrorType.Forbidden, "Требуется активный принятый администратор.");
        }
        catch (InvalidOperationException exception) when (exception.Message is "IdempotencyKeyReuse" or "PendingQueueFull"
            or "PendingRequiresReconciliation")
        {
            return Conflict();
        }
    }

    private async Task<ServiceError?> ReauthenticateAsync(string password, CancellationToken token)
    {
        verifiedActorRevision = null;
        ServiceError? error = AccessError();
        if (error is not null)
        {
            return error;
        }

        if (string.IsNullOrEmpty(password) || password.Length > 1024)
        {
            return new ServiceError(ServiceErrorType.Validation, "Требуется пароль действующего администратора.");
        }

        AcceptedValue value = state.Current!.Values[user.UserId];
        bool valid = await login.CheckAsync(RevisionAccounts.Materialize(value), password, token);
        error = AccessError();
        if (error is not null)
        {
            return error;
        }

        if (!valid || state.Find(user.UserId)?.Revision != value.Revision)
        {
            return new ServiceError(ServiceErrorType.Forbidden, "Повторная проверка администратора не пройдена.");
        }

        verifiedActorRevision = value.Revision;
        return null;
    }

    private ServiceError? AccessError()
    {
        ServiceError? error = access.GetError();
        if (error is not null)
        {
            return error;
        }

        if (!OnboardingProtocol.Supported(configured.Value))
        {
            return new ServiceError(ServiceErrorType.Conflict, "Приглашения требуют согласованного обновления узлов.");
        }

        if (verifiedActorRevision.HasValue && state.Find(user.UserId)?.Revision != verifiedActorRevision.Value)
        {
            return new ServiceError(ServiceErrorType.Forbidden, "Полномочия администратора изменились.");
        }

        return null;
    }

    private RevisionCommand Prepare(string action, Guid operationId, Guid invitationId, OnboardingCommand data)
    {
        RevisionImage image = state.Current!;
        return new RevisionCommand
        {
            Schema = 2, ClusterId = image.ClusterId, ClusterGeneration = image.Generation, OperationId = operationId,
            OriginNodeId = configured.Value.NodeId, ActorUserId = user.UserId,
            ActorRevision = verifiedActorRevision!.Value, EntityId = invitationId, EntityKind = DeletedEntityKind.Node,
            Action = action, BaseRevision = image.Onboarding?.Revision ?? 0, CreatedAtUtc = time.GetUtcNow(), Onboarding = data
        };
    }

    private async Task<bool> SubmitAsync(RevisionCommand command, CancellationToken token)
    {
        SubmissionReceipt submitted = await pending.SubmitAsync(command, configured.Value.NodeId, token);
        if (submitted.Result is not null)
        {
            return false;
        }

        PendingOperation? local = await work.PendingOperations.GetAsync(command.OperationId, token);
        if (local?.State == PendingState.NeedsUserConfirmation)
        {
            throw new InvalidOperationException("PendingRequiresReconciliation");
        }

        if (AccessError() is not null)
        {
            throw new UnauthorizedAccessException("OnboardingReauthenticationChanged");
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10), time);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try
        {
            IReadOnlyList<CommandResult> results = await transport.SendAsync([command], linked.Token);
            return results.Count == 1 && results[0].OperationId == command.OperationId && results[0].Outcome == OperationOutcome.Committed;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is HttpRequestException
            || exception is InvalidOperationException operation && operation.Message is "LeaderUnavailable" or "OnboardingUnavailable"
                or "OnboardingCoordinatorChanged" or "DecisionUnavailable")
        {
            return false;
        }
    }

    private InvitationResponse Response(Guid invitationId, Guid operationId, bool locallyPending)
    {
        RevisionImage image = state.Current!;
        image.Results.TryGetValue(operationId, out CommandResult? result);
        OnboardingInvitation? invitation = image.Onboarding?.Invitations.GetValueOrDefault(invitationId);
        string operationStatus = "Unconfirmed";
        if (locallyPending)
        {
            operationStatus = "LocalPending";
        }
        string? status = invitation?.Status.ToString();
        if (result is not null)
        {
            operationStatus = result.Outcome.ToString();
        }

        if (invitation?.Status == InvitationStatus.Unused && time.GetUtcNow() >= invitation.ExpiresAtUtc)
        {
            status = "Expired";
        }

        return new InvitationResponse(operationId, invitationId, operationStatus, status, invitation?.ExpiresAtUtc,
            invitation?.Attempt?.Request.AttemptId, invitation?.Attempt?.Status.ToString(), result?.Reason);
    }

    private static ServiceResult<InvitationResponse> Conflict()
    {
        return ServiceResult<InvitationResponse>.Fail(ServiceErrorType.Conflict, "OperationId уже связан с запросом. Используйте явную замену.");
    }

    private bool MatchesAccepted(RevisionImage image, Guid operationId, Guid invitationId, Guid attemptId,
        string action, Guid? replacesId)
    {
        OnboardingRequestReceipt? receipt = image.Onboarding?.Requests.GetValueOrDefault(operationId);
        return receipt is not null && receipt.InvitationId == invitationId && receipt.AttemptId == attemptId
            && receipt.ActorId == user.UserId && receipt.Action == action && receipt.ReplacesInvitationId == replacesId;
    }

    private static ServiceResult<InvitationResponse> Invalid()
    {
        return ServiceResult<InvitationResponse>.Fail(ServiceErrorType.Validation, "Проверьте идентификаторы и состояние приглашения.");
    }

    private ServiceResult<InvitationResponse> Denied(Guid operationId, Guid invitationId, ServiceError error)
    {
        Guid actorId = Guid.Empty;
        if (user.IsAuthenticated)
        {
            actorId = user.UserId;
        }

        audit?.Rejected(operationId, invitationId, actorId);
        return ServiceResult<InvitationResponse>.Fail(error);
    }
}
