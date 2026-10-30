using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DotNext.Net.Cluster;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Общий Administrator подаёт эксплуатационные команды тем же защищённым Pending-путём.</summary>
public class ClusterOperationsAdministration(ClusterAdministrationAccess access, RevisionStateStore state,
    PendingCommandService pending, IPendingControlUnitOfWork work, IRevisionPayloadProtection protection,
    IOptions<ClusterOptions> options, TimeProvider time, IUserAccessContext user)
{
    public ServiceResult<ClusterOperationsStatus> Read()
    {
        ServiceError? error = access.GetError();
        if (error is not null)
        {
            return ServiceResult<ClusterOperationsStatus>.Fail(error);
        }

        ClusterOperationsImage image = state.Current!.Operations;
        ClusterOperationsStatus status = new(image.Revision, image.MembershipVersion, image.MaintenanceId, image.BotId,
            image.Nodes.Values.ToArray(), image.Credentials.Values.Select(item => new ClusterTrustStatus(item.Id, item.NodeId,
                item.Fingerprint, item.ExpiresAtUtc, item.AdmissionExpiresAtUtc, item.ConsumedAtUtc, item.RevokedAtUtc)).ToArray(),
            image.Changes.Values.ToArray());
        return ServiceResult<ClusterOperationsStatus>.Ok(status);
    }

    public async Task<ServiceResult<AdministrationOperationDto>> SubmitAsync(ClusterOperationRequest request, CancellationToken token)
    {
        ServiceError? error = access.GetError();
        if (error is not null)
        {
            return ServiceResult<AdministrationOperationDto>.Fail(error);
        }

        if (request.Action is not ("AdmitNode" or "RequestMembership" or "AuthorizeMembership" or "TrustCredential" or "RevokeCredential" or "EnterMaintenance" or "Cancel" or "Replace"))
        {
            return ServiceResult<AdministrationOperationDto>.Fail(ServiceErrorType.Validation, "Неизвестная административная операция.");
        }

        try
        {
            PendingOperation? prior = await work.PendingOperations.GetAsync(request.OperationId, token);
            RevisionCommand? original = null;
            DateTimeOffset createdAt = time.GetUtcNow();
            if (prior is not null)
            {
                original = RevisionCodec.Decode(protection.Unprotect(prior.ProtectedPayload));
                createdAt = original.CreatedAtUtc;
            }

            RevisionCommand command;
            if (request.Action is "Cancel" or "Replace")
            {
                command = await PrepareControlAsync(request, createdAt, token);
            }
            else
            {
                command = Prepare(request, state.Current!, createdAt);
            }
            if (original is not null)
            {
                command = command with { ActorUserId = original.ActorUserId, ActorRevision = original.ActorRevision };
                if (RevisionCodec.Digest(command) != RevisionCodec.Digest(original))
                {
                    return ServiceResult<AdministrationOperationDto>.Fail(ServiceErrorType.Conflict, "OperationId уже связан с другим запросом.");
                }

                command = original;
            }

            SubmissionReceipt receipt;
            if (request.ConfirmRestored)
            {
                receipt = await pending.ConfirmRestoredAsync(request.TargetOperationId ?? throw new ArgumentException("TargetMissing"),
                    command, options.Value.NodeId, token);
            }
            else
            {
                receipt = await pending.SubmitAsync(command, options.Value.NodeId, token);
            }

            return ServiceResult<AdministrationOperationDto>.Ok(AdministrationMapping.Operation(receipt, state.Current!));
        }
        catch (UnauthorizedAccessException)
        {
            return ServiceResult<AdministrationOperationDto>.Fail(ServiceErrorType.Forbidden, "Требуется активный принятый администратор.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or CryptographicException)
        {
            return ServiceResult<AdministrationOperationDto>.Fail(ServiceErrorType.Validation, "Проверьте идентичность узла, сертификат и ревизию.");
        }
        catch (InvalidOperationException exception) when (exception.Message is "IdempotencyKeyReuse" or "PendingQueueFull"
            or "PendingRequiresReconciliation" or "StaleConfirmation" or "PendingTargetMissing" or "ExplicitReplacementRequired")
        {
            return ServiceResult<AdministrationOperationDto>.Fail(ServiceErrorType.Conflict, "Заявка не изменена. Проверьте OperationId и очередь.");
        }
    }

    private async Task<RevisionCommand> PrepareControlAsync(ClusterOperationRequest request, DateTimeOffset at, CancellationToken token)
    {
        Guid targetId = request.TargetOperationId ?? throw new ArgumentException("TargetMissing");
        PendingOperation target = await work.PendingOperations.GetAsync(targetId, token)
            ?? throw new ArgumentException("TargetMissing");
        RevisionCommand original = RevisionCodec.Decode(protection.Unprotect(target.ProtectedPayload));
        if (!ClusterOperationValidation.IsOperation(original) || ClusterOperationValidation.IsInternal(original)
            || request.NodeId != original.EntityId)
        {
            throw new ArgumentException("NotAnAdministrativeLifecycleTarget");
        }

        RevisionCommand? replacement = null;
        if (request.Action == "Replace")
        {
            string action = request.ReplacementAction ?? throw new ArgumentException("ReplacementActionMissing");
            if (action is not ("AdmitNode" or "RequestMembership" or "AuthorizeMembership" or "TrustCredential" or "RevokeCredential" or "EnterMaintenance"))
            {
                throw new ArgumentException("ReplacementActionInvalid");
            }

            replacement = Prepare(request with
            {
                Action = action, OperationId = request.ReplacementOperationId ?? throw new ArgumentException("ReplacementIdMissing")
            }, state.Current!, at);
        }

        RevisionImage image = state.Current!;
        return original with
        {
            OperationId = request.OperationId, Action = request.Action, CreatedAtUtc = at,
            ActorUserId = user.UserId, ActorRevision = state.Find(user.UserId)?.Revision ?? throw new UnauthorizedAccessException(),
            BaseRevision = image.Operations.Revision, Value = null, Operation = null,
            Target = original, Replacement = replacement
        };
    }

    private RevisionCommand Prepare(ClusterOperationRequest request, RevisionImage image, DateTimeOffset at)
    {
        ClusterOptions local = options.Value;
        ClusterOperation payload = new()
        {
            StorageGeneration = local.StorageGeneration, MembershipVersion = request.MembershipVersion,
            Remove = request.Remove, ChangeId = request.CredentialId, MaintenanceId = request.MaintenanceId
        };
        if (request.Action == "AuthorizeMembership")
        {
            payload = payload with { ChangeId = request.MembershipChangeId };
        }

        if (request.Action is "AdmitNode" or "TrustCredential")
        {
            using X509Certificate2 certificate = X509Certificate2.CreateFromPem(request.PublicCertificate
                ?? throw new ArgumentException("PublicCertificateMissing"));
            using X509Certificate2 root = X509CertificateLoader.LoadCertificateFromFile(local.ClientCaPath);
            string fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            string address = request.Address ?? image.Operations.Nodes.GetValueOrDefault(request.NodeId)?.Address
                ?? throw new ArgumentException("AddressMissing");
            ClusterOperationValidation.ValidateAddress(address);
            PeerCredential peer = new(request.CredentialId, request.NodeId, request.StorageGeneration,
                new Uri(address), fingerprint, false, true, new DateTimeOffset(certificate.NotAfter.ToUniversalTime()));
            if (!new PeerCertificateValidator().Validate(certificate, root, local, peer, time.GetUtcNow()))
            {
                throw new ArgumentException("PeerCertificateInvalid");
            }

            Guid? admissionId = null;
            DateTimeOffset? admissionExpires = null;
            if (request.Action == "AdmitNode")
            {
                admissionId = request.OperationId;
                admissionExpires = at.AddMinutes(15);
                string raftId = ClusterMemberId.FromEndPoint(new UriEndPoint(new Uri(address))).ToString();
                payload = payload with { Node = new(request.NodeId, request.StorageGeneration, address, raftId, false, false, false, 0) };
            }

            payload = payload with { Credential = new(request.CredentialId, request.NodeId, fingerprint,
                certificate.ExportCertificatePem(), peer.ExpiresAtUtc, admissionId, admissionExpires) };
        }

        RevisionCommand command = new()
        {
            ClusterId = image.ClusterId, ClusterGeneration = image.Generation, OperationId = request.OperationId,
            OriginNodeId = local.NodeId, ActorUserId = user.UserId,
            ActorRevision = state.Find(user.UserId)?.Revision ?? throw new UnauthorizedAccessException(),
            EntityId = request.NodeId, EntityKind = DeletedEntityKind.Node, Action = request.Action,
            BaseRevision = request.BaseRevision, CreatedAtUtc = at, Operation = payload
        };
        RevisionCodec.Validate(command);
        return command;
    }
}
