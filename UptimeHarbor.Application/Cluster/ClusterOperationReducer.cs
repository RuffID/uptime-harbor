using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Application.Cluster.Onboarding;

namespace UptimeHarbor.Application.Cluster;

/// <summary>Принимает эксплуатационные решения в существующем журнале, без собственного consensus.</summary>
public static class ClusterOperationReducer
{
    public static RevisionImage Apply(RevisionImage image, RevisionCommand command)
    {
        return ApplyCore(image, command, false);
    }

    internal static RevisionImage ApplyDelegated(RevisionImage image, RevisionCommand command)
    {
        return ApplyCore(image, command, true);
    }

    private static RevisionImage ApplyCore(RevisionImage image, RevisionCommand command, bool delegated)
    {
        ClusterOperation payload = command.Operation!;
        ClusterOperationsImage operations = image.Operations;
        if (!image.Monitoring.Nodes.TryGetValue(command.OriginNodeId, out Guid storage)
            || storage != payload.StorageGeneration)
        {
            return Decide(image, command, "LifecycleOriginNotAdmitted");
        }

        if (ClusterOperationValidation.IsInternal(command))
        {
            OperationalNode? origin = operations.Nodes.GetValueOrDefault(command.OriginNodeId);
            if (origin is null || !origin.Voting || origin.Removed || payload.CoordinatorTerm != image.Term)
            {
                return Decide(image, command, "LifecycleAuthorityChanged");
            }
        }
        else
        {
            AcceptedAccount? actor = RevisionAccounts.Find(image, command.ActorUserId);
            if (!AcceptedAdministratorAccessPolicy.IsActiveAdministrator(actor) || actor!.Revision != command.ActorRevision)
            {
                return Decide(image, command, "ActorNotActive");
            }
        }

        if (command.BaseRevision != operations.Revision)
        {
            return Decide(image, command, "OperationsRevisionChanged");
        }

        if (command.Action is not ("ConfirmMembership" or "AuthorizeMembership")
            && payload.MembershipVersion != operations.MembershipVersion)
        {
            return Decide(image, command, "MembershipVersionChanged");
        }

        OperationalNode? node = operations.Nodes.GetValueOrDefault(command.EntityId);
        bool addingMember = command.Action == "RequestMembership" && !payload.Remove;
        if (command.Action == "AuthorizeMembership")
        {
            addingMember = operations.Changes.GetValueOrDefault(payload.ChangeId)?.Remove == false;
        }

        if (addingMember)
        {
            if (!delegated && OnboardingMembership.Find(image, command.EntityId) is not null)
            {
                return Decide(image, command, "OnboardingProtocolRequired");
            }

            if (!OnboardingMembership.CanContinue(image, command.EntityId,
                command.ActorUserId, command.ActorRevision, command.CreatedAtUtc))
            {
                return Decide(image, command, "OnboardingDelegationChanged");
            }
        }

        string? error = null;
        switch (command.Action)
        {
            case "AdmitNode":
                error = AdmitAccepted(image, command, out image);
                break;
            case "RequestMembership":
                if (node is null || node.Removed || payload.Remove && !node.Voting
                    || operations.Changes.Values.Any(change => !change.ConfirmedVersion.HasValue))
                {
                    error = "MembershipChangeUnavailable";
                    break;
                }

                if (payload.Remove && operations.Nodes.Values.Count(member => member.Voting && !member.Removed) <= 1)
                {
                    error = "LastVoter";
                    break;
                }

                if (!payload.Remove && (node.Voting || !AdmissionValid(operations, node.NodeId, command.CreatedAtUtc)))
                {
                    error = "AdmissionUnavailable";
                    break;
                }

                MembershipOperation change = new(command.OperationId, command.ActorUserId, command.ActorRevision,
                    node.NodeId, payload.Remove, payload.MembershipVersion, null, image.Index)
                {
                    RequestedAtUtc = command.CreatedAtUtc
                };
                image = image with { Operations = operations with { Changes = operations.Changes.Add(change.Id, change), Sender = null } };
                break;
            case "AuthorizeMembership":
                MembershipOperation? pending = operations.Changes.GetValueOrDefault(payload.ChangeId);
                if (pending is null || pending.ConfirmedVersion.HasValue || pending.NodeId != command.EntityId
                    || payload.MembershipVersion < pending.ExpectedVersion)
                {
                    error = "MembershipChangeUnavailable";
                    break;
                }

                image = image with { Operations = operations with
                {
                    Changes = operations.Changes.SetItem(pending.Id, pending with
                    {
                        ActorUserId = command.ActorUserId, ActorRevision = command.ActorRevision, Revision = image.Index
                    })
                } };
                break;
            case "ConfirmMembership":
                error = Confirm(image, command, delegated, out image);
                break;
            case "ActivateNode":
                if (OnboardingMembership.Find(image, command.EntityId) is not null
                    || image.OnboardingContracts?.GenesisInitialized == true && image.OnboardingContracts.Genesis?.NodeId == command.EntityId)
                {
                    error = "OnboardingActivationRequired";
                    break;
                }

                if (node is null || !node.Voting || node.Removed || operations.MaintenanceId != Guid.Empty)
                {
                    error = "ActivationUnavailable";
                    break;
                }

                image = image with { Operations = operations with { Nodes = operations.Nodes.SetItem(node.NodeId,
                    node with { Activated = true, Revision = image.Index }) } };
                break;
            case "TrustCredential":
                OperationalCredential credential = payload.Credential ?? throw new InvalidDataException("CredentialMissing");
                if (node is null || node.Removed || credential.NodeId != node.NodeId || credential.AdmissionId.HasValue
                    || operations.Credentials.ContainsKey(credential.Id)
                    || operations.Credentials.Values.Any(item => item.Fingerprint == credential.Fingerprint))
                {
                    error = "CredentialUnavailable";
                    break;
                }

                image = image with { Operations = operations with { Credentials = operations.Credentials.Add(credential.Id, credential) } };
                break;
            case "RevokeCredential":
                OperationalCredential? revoked = operations.Credentials.GetValueOrDefault(payload.ChangeId);
                if (revoked is null || revoked.NodeId != command.EntityId)
                {
                    error = "CredentialMissing";
                    break;
                }

                image = image with { Operations = operations with { Credentials = operations.Credentials.SetItem(revoked.Id,
                    revoked with { RevokedAtUtc = command.CreatedAtUtc }), Sender = null } };
                break;
            case "EnterMaintenance":
                if (payload.MaintenanceId == Guid.Empty)
                {
                    error = "MaintenanceIdentityMissing";
                    break;
                }

                image = image with { Operations = operations with { MaintenanceId = payload.MaintenanceId, Sender = null } };
                break;
            case "LeaveMaintenance":
                if (payload.MaintenanceId == Guid.Empty || payload.MaintenanceId != operations.MaintenanceId)
                {
                    error = "MaintenanceGenerationMismatch";
                    break;
                }

                image = image with { Operations = operations with { MaintenanceId = Guid.Empty, Sender = null } };
                break;
            case "OpenSenderEpoch":
                SenderAuthorityKey? sender = payload.Sender;
                if (sender is null || sender.ClusterId != image.ClusterId || sender.Generation != image.Generation
                    || sender.Term != image.Term || sender.LeaderNodeId != command.OriginNodeId
                    || sender.SenderEpoch == Guid.Empty || operations.MaintenanceId != Guid.Empty
                    || operations.Changes.Values.Any(item => !item.ConfirmedVersion.HasValue))
                {
                    error = "SenderEpochUnavailable";
                    break;
                }

                image = image with { Operations = operations with { Sender = sender } };
                break;
            default:
                throw new InvalidDataException("UnsupportedLifecycleOperation");
        }

        if (error is null)
        {
            image = image with { Operations = image.Operations with { Revision = image.Index } };
        }

        return Decide(image, command, error);
    }

    internal static string? AdmitAccepted(RevisionImage image, RevisionCommand command, out RevisionImage result)
    {
        result = image;
        OperationalNode node = command.Operation!.Node!;
        OperationalCredential credential = command.Operation.Credential!;
        ClusterOperationsImage operations = image.Operations;
        bool duplicateIdentity = operations.Nodes.ContainsKey(node.NodeId)
            || operations.Nodes.Values.Any(item => item.StorageGeneration == node.StorageGeneration
                || !item.Removed && item.Address == node.Address);
        bool duplicateCredential = operations.Credentials.ContainsKey(credential.Id)
            || operations.Credentials.Values.Any(item => item.Fingerprint == credential.Fingerprint);
        if (duplicateIdentity || duplicateCredential)
        {
            return "DuplicateNodeIdentity";
        }

        result = image with
        {
            Operations = operations with
            {
                Nodes = operations.Nodes.Add(node.NodeId, node with { Revision = image.Index }),
                Credentials = operations.Credentials.Add(credential.Id, credential)
            },
            Monitoring = image.Monitoring with { Nodes = image.Monitoring.Nodes.Add(node.NodeId, node.StorageGeneration) }
        };
        return null;
    }

    private static bool AdmissionValid(ClusterOperationsImage operations, Guid nodeId, DateTimeOffset at)
    {
        return operations.Credentials.Values.Any(item => item.NodeId == nodeId && item.AdmissionId.HasValue
            && !item.ConsumedAtUtc.HasValue && !item.RevokedAtUtc.HasValue && item.AdmissionExpiresAtUtc > at && item.ExpiresAtUtc > at);
    }

    private static string? Confirm(RevisionImage image, RevisionCommand command, bool delegated, out RevisionImage result)
    {
        result = image;
        ClusterOperation payload = command.Operation!;
        ClusterOperationsImage operations = image.Operations;
        MembershipOperation? change = operations.Changes.GetValueOrDefault(payload.ChangeId);
        if (change is null || change.NodeId != command.EntityId || change.ConfirmedVersion.HasValue
            || payload.MembershipVersion <= change.ExpectedVersion)
        {
            return "MembershipResultMismatch";
        }

        AcceptedAccount? actor = RevisionAccounts.Find(image, change.ActorUserId);
        if (!AcceptedAdministratorAccessPolicy.IsActiveAdministrator(actor) || actor!.Revision != change.ActorRevision)
        {
            return "ActorNotActive";
        }

        if (!change.Remove && !OnboardingMembership.CanContinue(image, change.NodeId,
            change.ActorUserId, change.ActorRevision, command.CreatedAtUtc))
        {
            return "OnboardingDelegationChanged";
        }

        if (!change.Remove && !delegated && OnboardingMembership.Find(image, change.NodeId) is not null)
        {
            return "OnboardingProtocolRequired";
        }

        OperationalNode node = operations.Nodes[change.NodeId];
        operations = operations with
        {
            Nodes = operations.Nodes.SetItem(node.NodeId, node with { Voting = !change.Remove,
                Removed = change.Remove, Activated = false, Revision = image.Index }),
            Changes = operations.Changes.SetItem(change.Id, change with { ConfirmedVersion = payload.MembershipVersion, Revision = image.Index }),
            Sender = null, MembershipVersion = payload.MembershipVersion
        };
        foreach (OperationalCredential credential in operations.Credentials.Values.Where(item => item.NodeId == node.NodeId))
        {
            OperationalCredential updated = credential;
            if (change.Remove)
            {
                updated = credential with { RevokedAtUtc = command.CreatedAtUtc };
            }
            else if (credential.AdmissionId.HasValue)
            {
                updated = credential with { ConsumedAtUtc = change.RequestedAtUtc };
            }

            operations = operations with { Credentials = operations.Credentials.SetItem(updated.Id, updated) };
        }

        result = OnboardingMembership.Confirm(image with { Operations = operations }, node.NodeId, change.Remove);
        return null;
    }

    private static RevisionImage Decide(RevisionImage image, RevisionCommand command, string? error)
    {
        OperationOutcome outcome = OperationOutcome.Committed;
        if (error is not null)
        {
            outcome = OperationOutcome.Rejected;
        }

        CommandResult result = new(command.OperationId, RevisionCodec.Digest(command), outcome, image.Index, image.Term,
            image.Index, error, null, null, command.ActorRevision);
        return image with { Results = image.Results.Add(result.OperationId, result) };
    }
}
