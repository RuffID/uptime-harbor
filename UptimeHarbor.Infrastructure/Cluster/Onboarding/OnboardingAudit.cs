using Microsoft.Extensions.Logging;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

public class OnboardingAudit(ILogger<OnboardingAudit> logger) : IOnboardingAudit
{
    public void Rejected(Guid operationId, Guid invitationId, Guid actorId)
    {
        logger.LogWarning("Onboarding request rejected {OperationId} {InvitationId} {ActorId}.", operationId, invitationId, actorId);
    }

    public void Accepted(RevisionImage before, RevisionImage after, RevisionCommand command, CommandResult result)
    {
        if (command.Action == VoterContinuationCommands.BIND)
        {
            AuditContinuationBind(before, after, command, result);
            return;
        }

        if (VoterCredentialCommands.IsRotation(command))
        {
            if (before.Results.ContainsKey(command.OperationId))
            {
                return;
            }

            if (after.Results.GetValueOrDefault(command.OperationId) != result
                || result.Digest != RevisionCodec.Digest(command))
            {
                throw new InvalidDataException("VoterCredentialAuditDecisionUnconfirmed");
            }

            string eventName = "CertificateRejected";
            if (result.Outcome == OperationOutcome.Committed)
            {
                VoterCredentialRotation rotation = after.OnboardingContracts?.VoterCredentialRotations?
                    .GetValueOrDefault(command.EnrollmentTrust!.Rotation!.Renewal.Issuance.IssueId)
                    ?? throw new InvalidDataException("VoterCredentialAuditStateMissing");
                Guid? operationId;
                long? index;
                if (command.Action == "AuthorizeVoterCredential")
                {
                    eventName = "CertificateAuthorized";
                    operationId = rotation.AuthorizationOperationId;
                    index = rotation.AuthorizedIndex;
                }
                else if (command.Action == "BindVoterCredential")
                {
                    eventName = "CertificateBound";
                    operationId = rotation.BindOperationId;
                    index = rotation.CredentialRevision;
                }
                else
                {
                    eventName = "CertificateRetired";
                    operationId = rotation.RetireOperationId;
                    index = rotation.RetiredIndex;
                }

                if (operationId != command.OperationId || index != result.Index)
                {
                    throw new InvalidDataException("VoterCredentialAuditBoundaryUnconfirmed");
                }
            }

            VoterCredentialRenewal renewal = command.EnrollmentTrust!.Rotation!.Renewal;
            logger.LogInformation("Enrollment {EventName} {OperationId} {NodeId} {CredentialId} {SystemSource} {Outcome} at {Index}.",
                eventName, command.OperationId, renewal.Issuance.NodeId, renewal.Issuance.IssueId,
                "VoterCredentialLifecycle", result.Outcome, result.Index);
            return;
        }

        if (OnlineLifecycleCommands.IsOnline(command))
        {
            OnlineGrant grant = command.OnlineLifecycle!.Grant;
            logger.LogInformation("Online lifecycle {Action} {OperationId} {GrantId} {NodeId} {ActorId} {Outcome} at {Index}.",
                command.Action, command.OperationId, grant.GrantId, grant.NodeId, grant.ActorId, result.Outcome, result.Index);
            return;
        }

        if (EnrollmentTrustCommands.IsTrust(command))
        {
            if (command.Action == "RegisterEnrollmentIssuer")
            {
                string eventName = "IssuerRejected";
                if (result.Outcome == OperationOutcome.Committed)
                {
                    eventName = "IssuerRegistered";
                }

                logger.LogInformation("Enrollment {EventName} {OperationId} {IssuerEpoch} {ActorId} {Outcome} at {Index}.",
                    eventName, command.OperationId, command.EnrollmentTrust!.IssuerEpoch, command.ActorUserId, result.Outcome, result.Index);
                return;
            }

            logger.LogInformation("Enrollment trust {Action} {OperationId} {IssuerEpoch} {ActorId} {Outcome} at {Index}.",
                command.Action, command.OperationId, command.EnrollmentTrust!.IssuerEpoch, command.ActorUserId, result.Outcome, result.Index);
            return;
        }

        if (result.Outcome != OperationOutcome.Committed)
        {
            logger.LogWarning("Onboarding rejected {OperationId} {InvitationId} {AttemptId} {ActorId} at {Index}.",
                command.OperationId, command.Onboarding!.InvitationId, command.Onboarding.AttemptId, command.ActorUserId, result.Index);
        }

        if (after.Onboarding is null)
        {
            return;
        }

        foreach (OnboardingInvitation invitation in after.Onboarding.Invitations.Values)
        {
            OnboardingInvitation? previous = before.Onboarding?.Invitations.GetValueOrDefault(invitation.Id);
            if (previous == invitation)
            {
                continue;
            }

            string eventName = "OnboardingProgressAccepted";
            if (previous is null)
            {
                eventName = "InvitationIssued";
                if (command.Action == "ReplaceInvitation")
                {
                    eventName = "InvitationReplaced";
                }
            }
            else if (previous.Status != invitation.Status)
            {
                switch (invitation.Status)
                {
                    case InvitationStatus.Reserved:
                        eventName = "InvitationReserved";
                        break;
                    case InvitationStatus.Consumed:
                        eventName = "InvitationConsumed";
                        break;
                    case InvitationStatus.Revoked:
                        eventName = "InvitationRevoked";
                        break;
                    case InvitationStatus.Expired:
                        eventName = "InvitationExpired";
                        break;
                }
            }
            else if (invitation.Attempt?.Status == OnboardingAttemptStatus.PauseAwaitingAdministrator)
            {
                eventName = "AttemptPaused";
            }
            else if (previous.Attempt?.Status == OnboardingAttemptStatus.PauseAwaitingAdministrator)
            {
                eventName = "AttemptContinued";
            }

            Guid actorId = command.ActorUserId;
            if (actorId == Guid.Empty)
            {
                actorId = invitation.Delegation.ActorId;
                if (invitation.Attempt is not null)
                {
                    actorId = invitation.Attempt.Delegation.ActorId;
                }
            }

            logger.LogInformation("Onboarding {EventName} {OperationId} {InvitationId} {AttemptId} {NodeId} {ActorId} at {Index}.",
                eventName, command.OperationId, invitation.Id, invitation.Attempt?.Request.AttemptId,
                invitation.Attempt?.Request.NodeId, actorId, result.Index);
        }
    }

    private void AuditContinuationBind(RevisionImage before, RevisionImage after, RevisionCommand command, CommandResult result)
    {
        if (before.Results.ContainsKey(command.OperationId))
        {
            return;
        }

        if (after.Results.GetValueOrDefault(command.OperationId) != result
            || RevisionDispatcher.ReadVoterCredentialDecision(after, command) != result)
        {
            throw new InvalidDataException("VoterContinuationBindAuditDecisionUnconfirmed");
        }

        VoterCredentialRequest request = command.VoterContinuation!.Context.Request;
        if (result.Outcome == OperationOutcome.Committed)
        {
            if (!VoterContinuationBinding.IsCurrentBinding(after, command))
            {
                throw new InvalidDataException("VoterContinuationBindAuditBoundaryUnconfirmed");
            }

            logger.LogInformation("Enrollment CertificateBound by continuation {BindId} {RequestId} {IssueId} {NodeId} {ActorId} at {Index}.",
                command.OperationId, request.OperationId, request.Renewal.Issuance.IssueId, command.EntityId, command.ActorUserId, result.Index);
        }
        else
        {
            logger.LogWarning("Enrollment CertificateRejected by continuation {BindId} {RequestId} {IssueId} {NodeId} {ActorId} at {Index}.",
                command.OperationId, request.OperationId, request.Renewal.Issuance.IssueId, command.EntityId, command.ActorUserId, result.Index);
        }
    }
}
