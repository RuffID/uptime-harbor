using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

public static class ClusterOperationValidation
{
    public static bool IsOperation(RevisionCommand command)
    {
        return command.Action is "AdmitNode" or "RequestMembership" or "ConfirmMembership" or "ActivateNode"
            or "TrustCredential" or "RevokeCredential" or "EnterMaintenance" or "LeaveMaintenance" or "OpenSenderEpoch" or "AuthorizeMembership";
    }

    public static bool IsInternal(RevisionCommand command)
    {
        return command.Action is "ConfirmMembership" or "OpenSenderEpoch" or "ActivateNode" or "LeaveMaintenance";
    }

    public static void Validate(RevisionCommand command)
    {
        ClusterOperation operation = command.Operation ?? throw new InvalidDataException("OperationPayloadMissing");
        if (command.Value is not null || command.Target is not null || command.Replacement is not null
            || command.Monitoring is not null || command.Delivery is not null || command.ResolvesConflictId.HasValue
            || command.AccountAction is not null || command.EntityKind != DeletedEntityKind.Node)
        {
            throw new InvalidDataException("InvalidOperationEnvelope");
        }

        StorageGuards.Id(operation.StorageGeneration, nameof(operation.StorageGeneration));
        ArgumentOutOfRangeException.ThrowIfNegative(operation.MembershipVersion);
        bool credentialAction = command.Action is "AdmitNode" or "TrustCredential";
        if (credentialAction != (operation.Credential is not null)
            || (command.Action == "AdmitNode") != (operation.Node is not null)
            || (command.Action == "OpenSenderEpoch") != (operation.Sender is not null)
            || (command.Action is "EnterMaintenance" or "LeaveMaintenance") != (operation.MaintenanceId != Guid.Empty)
            || (command.Action is "ConfirmMembership" or "RevokeCredential" or "AuthorizeMembership") != (operation.ChangeId != Guid.Empty)
            || command.Action != "RequestMembership" && operation.Remove)
        {
            throw new InvalidDataException("UnexpectedLifecycleFields");
        }

        if (IsInternal(command))
        {
            if (command.ActorUserId != Guid.Empty || command.ActorRevision != 0 || command.SystemSource != "ClusterLifecycle")
            {
                throw new InvalidDataException("InvalidLifecycleSource");
            }
        }
        else
        {
            StorageGuards.Id(command.ActorUserId, nameof(command.ActorUserId));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.ActorRevision);
            if (command.SystemSource is not null)
            {
                throw new InvalidDataException("UnexpectedLifecycleSource");
            }
        }

        if (command.Action == "AdmitNode")
        {
            OperationalNode node = operation.Node ?? throw new InvalidDataException("NodeMissing");
            OperationalCredential credential = operation.Credential ?? throw new InvalidDataException("CredentialMissing");
            StorageGuards.Id(node.NodeId, nameof(node.NodeId));
            StorageGuards.Id(node.StorageGeneration, nameof(node.StorageGeneration));
            if (node.NodeId != command.EntityId || node.Voting || node.Activated || node.Removed
                || credential.NodeId != node.NodeId || !credential.AdmissionId.HasValue
                || credential.AdmissionId != command.OperationId || node.Revision != 0
                || string.IsNullOrWhiteSpace(node.RaftMemberId)
                || credential.AdmissionExpiresAtUtc != command.CreatedAtUtc.AddMinutes(15))
            {
                throw new InvalidDataException("InvalidNodeAdmission");
            }

            ValidateAddress(node.Address);
        }

        if (command.Action == "TrustCredential" && (operation.Credential!.AdmissionId.HasValue
            || operation.Credential.AdmissionExpiresAtUtc.HasValue || operation.Credential.NodeId != command.EntityId))
        {
            throw new InvalidDataException("UnexpectedAdmissionCredential");
        }

        if (operation.Credential is not null)
        {
            OperationalCredential credential = operation.Credential;
            StorageGuards.Id(credential.Id, nameof(credential.Id));
            StorageGuards.Id(credential.NodeId, nameof(credential.NodeId));
            StorageGuards.Digest(credential.Fingerprint);
            StorageGuards.PublicCertificate(credential.PublicCertificate);
            StorageGuards.Utc(credential.ExpiresAtUtc);
            if (credential.ExpiresAtUtc <= command.CreatedAtUtc || credential.RevokedAtUtc.HasValue
                || credential.ConsumedAtUtc.HasValue)
            {
                throw new InvalidDataException("InvalidCredentialLifetime");
            }
        }
    }

    public static void ValidateAddress(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.AbsoluteUri != address || uri.AbsolutePath != "/internal/raft" || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new InvalidDataException("InvalidPeerAddress");
        }
    }
}
