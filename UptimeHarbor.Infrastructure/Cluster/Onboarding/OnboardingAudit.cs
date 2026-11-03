using Microsoft.Extensions.Logging;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
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
}
