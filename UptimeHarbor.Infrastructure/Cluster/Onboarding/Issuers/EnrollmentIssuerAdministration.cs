using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Issuers;

/// <summary>Новый manual issuer проходит прежний protected Pending, accepted apply и повторный majority barrier.</summary>
public class EnrollmentIssuerAdministration(ClusterAdministrationAccess access, RevisionStateStore state,
    PendingCommandService pending, IPendingControlUnitOfWork work, IRevisionPayloadProtection protection,
    IRevisionTransport transport, ILocalAccountLogin login, IUserAccessContext user,
    EnrollmentIssuerProvisioning provisioning, IOptions<ClusterOptions> options, IOptions<InstallationOptions> installation,
    TimeProvider time, ILogger<EnrollmentIssuerAdministration> logger, IEnrollmentIssuerRegistrationBarrier? barrier = null)
{
    private long? actorRevision;
    private DateTimeOffset authenticatedAt;

    public async Task<ServiceResult<EnrollmentIssuerStatus>> RegisterAsync(Guid operationId, string password, CancellationToken token)
    {
        ServiceError? error = await ReauthenticateAsync(password, token);
        if (error is not null)
        {
            logger.LogWarning("Issuer provisioning denied {OperationId} {ActorId}.", operationId, user.UserId);
            return ServiceResult<EnrollmentIssuerStatus>.Fail(error);
        }

        if (operationId == Guid.Empty)
        {
            return ServiceResult<EnrollmentIssuerStatus>.Fail(ServiceErrorType.Validation, "Требуется OperationId.");
        }

        try
        {
            PendingOperation? local = await work.PendingOperations.GetAsync(operationId, token);
            if (AccessError() is ServiceError changed)
            {
                return ServiceResult<EnrollmentIssuerStatus>.Fail(changed);
            }

            RevisionCommand? original = Original(local, operationId);
            if (local is not null && original is null || state.Current!.Results.ContainsKey(operationId) && original is null)
            {
                return Conflict();
            }

            await ConfirmAsync(token);
            if (AccessError() is ServiceError afterBarrier)
            {
                return ServiceResult<EnrollmentIssuerStatus>.Fail(afterBarrier);
            }

            if (state.Current!.Results.ContainsKey(operationId))
            {
                return await StatusAsync(operationId, original!, actorRevision!.Value, token);
            }

            RevisionCommand command;
            if (original is not null)
            {
                command = original;
                EnrollmentIssuerReference existing = await provisioning.ReadAsync(operationId, token);
                if (!Matches(existing, command.EnrollmentTrust!.Issuer!))
                {
                    throw new InvalidDataException("LocalEnrollmentIssuerChanged");
                }
            }
            else
            {
                if (state.Current!.OnboardingContracts?.Issuers?.Count > 0)
                {
                    return Conflict();
                }

                EnrollmentIssuerReference issuer = await provisioning.PrepareAsync(operationId, token);
                if (AccessError() is ServiceError afterSave)
                {
                    return ServiceResult<EnrollmentIssuerStatus>.Fail(afterSave);
                }

                command = Prepare(issuer);
            }

            if (AccessError() is ServiceError beforePending)
            {
                return ServiceResult<EnrollmentIssuerStatus>.Fail(beforePending);
            }

            SubmissionReceipt receipt = await pending.SubmitAsync(command, options.Value.NodeId, token);
            local = await work.PendingOperations.GetAsync(operationId, token);
            if (AccessError() is ServiceError beforeTransport)
            {
                return ServiceResult<EnrollmentIssuerStatus>.Fail(beforeTransport);
            }

            if (local?.State == PendingState.NeedsUserConfirmation)
            {
                return Conflict();
            }

            if (receipt.Result is null)
            {
                await SendAsync(command, token);
            }

            return await ReadAsync(operationId, token);
        }
        catch (Exception exception) when (Unavailable(exception, token))
        {
            return ServiceResult<EnrollmentIssuerStatus>.Fail(ServiceErrorType.Conflict,
                "Регистрация issuer не подтверждена: требуются текущий leader, majority и capability всех voters. Повторите с тем же OperationId.");
        }
        catch (InvalidOperationException exception) when (exception.Message is "IdempotencyKeyReuse" or "PendingQueueFull"
            or "PendingRequiresReconciliation" or "IssuerProvisioningAlreadyPrepared")
        {
            return Conflict();
        }
        catch (UnauthorizedAccessException exception) when (exception.Message is "EnrollmentTrustReauthenticationChanged" or "AcceptedAdministratorRequired")
        {
            return ServiceResult<EnrollmentIssuerStatus>.Fail(ServiceErrorType.Forbidden, "Полномочия администратора изменились.");
        }
    }

    public async Task<ServiceResult<EnrollmentIssuerStatus>> ReadAsync(Guid operationId, CancellationToken token)
    {
        if (AccessError() is ServiceError error)
        {
            return ServiceResult<EnrollmentIssuerStatus>.Fail(error);
        }

        long revision = state.Find(user.UserId)!.Revision;
        PendingOperation? local = await work.PendingOperations.GetAsync(operationId, token);
        if (AccessError() is ServiceError changed)
        {
            return ServiceResult<EnrollmentIssuerStatus>.Fail(changed);
        }

        RevisionCommand? original = Original(local, operationId);
        if (local is not null && original is null || state.Current!.Results.ContainsKey(operationId) && original is null)
        {
            return Conflict();
        }

        if (original is null)
        {
            return ServiceResult<EnrollmentIssuerStatus>.Ok(new(operationId, operationId, "Unconfirmed", false, null, null));
        }

        if (state.Find(user.UserId)?.Revision != revision)
        {
            return ServiceResult<EnrollmentIssuerStatus>.Fail(ServiceErrorType.Forbidden, "Полномочия администратора изменились.");
        }

        return await StatusAsync(operationId, original, revision, token);
    }

    private async Task<ServiceResult<EnrollmentIssuerStatus>> StatusAsync(Guid operationId, RevisionCommand original,
        long expectedActorRevision, CancellationToken token)
    {
        CommandResult? result = state.Current!.Results.GetValueOrDefault(operationId);
        if (result is not null && result.Digest != RevisionCodec.Digest(original))
        {
            return Conflict();
        }

        bool ready = false;
        if (result?.Outcome == OperationOutcome.Committed)
        {
            EnrollmentIssuerReference local = await provisioning.ReadAsync(operationId, token);
            if (AccessError() is ServiceError changed)
            {
                return ServiceResult<EnrollmentIssuerStatus>.Fail(changed);
            }

            bool confirmed = false;
            try
            {
                await ConfirmAsync(token);
                confirmed = true;
            }
            catch (Exception exception) when (Unavailable(exception, token))
            {
                // Commit сохраняется; без нового majority готовность не подтверждена.
            }

            if (AccessError() is ServiceError afterBarrier)
            {
                return ServiceResult<EnrollmentIssuerStatus>.Fail(afterBarrier);
            }

            if (state.Find(user.UserId)?.Revision != expectedActorRevision)
            {
                return ServiceResult<EnrollmentIssuerStatus>.Fail(ServiceErrorType.Forbidden, "Полномочия администратора изменились.");
            }

            AcceptedEnrollmentIssuer? accepted = state.Current!.OnboardingContracts?.Issuers?.GetValueOrDefault(operationId);
            ready = confirmed && accepted is not null && Matches(local, accepted) && !accepted.RetiredAtUtc.HasValue && !accepted.RevokedAtUtc.HasValue
                && time.GetUtcNow() >= accepted.NotBeforeUtc && time.GetUtcNow() < accepted.NotAfterUtc;
        }

        string status = "LocalPending";
        if (result is not null)
        {
            status = result.Outcome.ToString();
        }

        return ServiceResult<EnrollmentIssuerStatus>.Ok(new(operationId, operationId, status, ready, result?.Index, result?.Reason));
    }

    private RevisionCommand? Original(PendingOperation? local, Guid operationId)
    {
        if (local is null)
        {
            return null;
        }

        RevisionCommand original = RevisionCodec.Decode(protection.Unprotect(local.ProtectedPayload));
        EnrollmentTrustCommand? trust = original.EnrollmentTrust;
        if (original.OperationId != operationId || original.Action != "RegisterEnrollmentIssuer" || original.ActorUserId != user.UserId
            || original.ClusterId != state.Current!.ClusterId || original.ClusterGeneration != state.Current.Generation
            || original.OriginNodeId != options.Value.NodeId || trust?.IssuerEpoch != operationId
            || trust.StorageGeneration != options.Value.StorageGeneration || trust.Issuer?.NodeId != options.Value.NodeId
            || trust.Issuer.StorageGeneration != options.Value.StorageGeneration || local.Digest != RevisionCodec.Digest(original))
        {
            return null;
        }

        return original;
    }

    private RevisionCommand Prepare(EnrollmentIssuerReference local)
    {
        using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(local.CertificateDer);
        AcceptedEnrollmentIssuer issuer = new(local.Epoch, local.NodeId, local.StorageGeneration, EnrollmentIssuer.PEER_PURPOSE,
            Convert.ToBase64String(local.CertificateDer), local.CertificateDigest, local.SpkiDigest,
            new(certificate.NotBefore.ToUniversalTime()), new(certificate.NotAfter.ToUniversalTime()), 0);
        RevisionImage image = state.Current!;

        return new RevisionCommand
        {
            Schema = 3, ClusterId = image.ClusterId, ClusterGeneration = image.Generation, OperationId = local.Epoch,
            OriginNodeId = options.Value.NodeId, ActorUserId = user.UserId, ActorRevision = actorRevision!.Value,
            EntityId = local.Epoch, EntityKind = DeletedEntityKind.Node, Action = "RegisterEnrollmentIssuer",
            BaseRevision = 0, CreatedAtUtc = time.GetUtcNow(), EnrollmentTrust = new(1, local.StorageGeneration,
                local.Epoch, issuer, null, null, null, 0, authenticatedAt)
        };
    }

    private async Task<ServiceError?> ReauthenticateAsync(string password, CancellationToken token)
    {
        actorRevision = null;
        if (AccessError() is ServiceError error)
        {
            return error;
        }

        if (string.IsNullOrEmpty(password) || password.Length > 1024)
        {
            return new(ServiceErrorType.Validation, "Требуется пароль действующего администратора.");
        }

        AcceptedValue actor = state.Current!.Values[user.UserId];
        bool valid = await login.CheckAsync(RevisionAccounts.Materialize(actor), password, token);
        if (AccessError() is ServiceError changed)
        {
            return changed;
        }

        if (!valid || state.Find(user.UserId)?.Revision != actor.Revision)
        {
            return new(ServiceErrorType.Forbidden, "Повторная проверка администратора не пройдена.");
        }

        actorRevision = actor.Revision;
        authenticatedAt = time.GetUtcNow();
        return null;
    }

    private ServiceError? AccessError()
    {
        if (access.GetError() is ServiceError error)
        {
            return error;
        }

        if (!options.Value.Enabled || options.Value.Compatibility != OnboardingProtocol.TRUST_COMPATIBILITY
            || installation.Value.InstallationIntent != "Legacy")
        {
            return new(ServiceErrorType.Conflict, "Требуется ручной кластер с согласованной enrollment schema.");
        }

        OperationalNode? owner = state.Current!.Operations.Nodes.GetValueOrDefault(options.Value.NodeId);
        if (owner is null || owner.Removed || !owner.Voting || owner.StorageGeneration != options.Value.StorageGeneration
            || state.Current.Operations.MaintenanceId != Guid.Empty)
        {
            return new(ServiceErrorType.Conflict, "Локальный issuer должен быть действующим voter.");
        }

        if (actorRevision.HasValue && (state.Find(user.UserId)?.Revision != actorRevision
            || time.GetUtcNow() - authenticatedAt > TimeSpan.FromMinutes(5) || time.GetUtcNow() < authenticatedAt))
        {
            return new(ServiceErrorType.Forbidden, "Повторная проверка администратора устарела.");
        }

        return null;
    }

    private async Task ConfirmAsync(CancellationToken token)
    {
        if (barrier is null)
        {
            throw new InvalidOperationException("IssuerVoterCapabilityUnavailable");
        }

        await barrier.ConfirmAsync(token);
    }

    private async Task SendAsync(RevisionCommand command, CancellationToken token)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10), time);
        using CancellationTokenSource guard = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try
        {
            await transport.SendAsync([command], guard.Token);
        }
        catch (Exception exception) when (Unavailable(exception, token))
        {
            // Только существующий durable Pending означает 202; transport response не доказывает apply.
        }
    }

    private static bool Unavailable(Exception error, CancellationToken token)
    {
        if (error is OperationCanceledException)
        {
            return !token.IsCancellationRequested;
        }

        return error is HttpRequestException or JsonException
            || error is InvalidOperationException operation && operation.Message is "IssuerVoterCapabilityUnavailable" or "IssuerVotersChanged"
                or "EnrollmentReadMajorityUnavailable" or "EnrollmentReadMajorityChanged" or "LeaderUnavailable" or "OnboardingUnavailable"
                or "OnboardingCoordinatorChanged" or "DecisionUnavailable" or "IssuerStorageConfigurationRequired";
    }

    private static bool Matches(EnrollmentIssuerReference local, AcceptedEnrollmentIssuer issuer)
    {
        return local.Epoch == issuer.Epoch && local.NodeId == issuer.NodeId && local.StorageGeneration == issuer.StorageGeneration
            && local.CertificateDigest == issuer.CertificateDigest && local.SpkiDigest == issuer.SpkiDigest
            && Convert.ToBase64String(local.CertificateDer) == issuer.CertificateDer;
    }

    private static ServiceResult<EnrollmentIssuerStatus> Conflict()
    {
        return ServiceResult<EnrollmentIssuerStatus>.Fail(ServiceErrorType.Conflict,
            "OperationId или локальный issuer уже связан с запросом. Используйте исходный OperationId.");
    }
}
