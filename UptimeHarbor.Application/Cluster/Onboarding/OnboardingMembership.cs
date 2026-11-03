using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Onboarding;

/// <summary>Повторная проверка исходной delegation при существующем membership pipeline.</summary>
public static class OnboardingMembership
{
    public static string? Apply(RevisionImage image, RevisionCommand command, OnboardingAttempt attempt,
        DateTimeOffset now, out RevisionImage result)
    {
        result = image;
        if (attempt.Status != OnboardingAttemptStatus.Admitted)
        {
            return "OnboardingMembershipUnavailable";
        }

        OnboardingCommand data = command.Onboarding!;
        bool confirm = command.Action == "ConfirmOnboardingMembership";
        string action = "RequestMembership";
        Guid actor = attempt.Delegation.ActorId;
        long actorRevision = attempt.Delegation.ActorRevision;
        string? source = null;
        long membership = image.Operations.MembershipVersion;
        Guid changeId = Guid.Empty;
        if (command.Action == "AuthorizeOnboardingMembership")
        {
            action = "AuthorizeMembership";
            changeId = data.MembershipChangeId!.Value;
        }

        if (confirm)
        {
            action = "ConfirmMembership";
            actor = Guid.Empty;
            actorRevision = 0;
            source = "ClusterLifecycle";
            membership = data.MembershipVersion;
            changeId = data.MembershipChangeId!.Value;
        }

        RevisionCommand delegated = command with
        {
            Schema = 1, Action = action, EntityId = attempt.Request.NodeId, ActorUserId = actor, ActorRevision = actorRevision,
            SystemSource = source, CreatedAtUtc = now, Onboarding = null, BaseRevision = image.Operations.Revision,
            Operation = new ClusterOperation
            {
                StorageGeneration = data.StorageGeneration, CoordinatorTerm = image.Term, MembershipVersion = membership, ChangeId = changeId
            }
        };
        RevisionImage reduced = ClusterOperationReducer.ApplyDelegated(image, delegated);
        CommandResult decision = reduced.Results[command.OperationId];
        result = reduced with { Results = reduced.Results.Remove(command.OperationId) };
        if (decision.Outcome != OperationOutcome.Committed)
        {
            return decision.Reason ?? "OnboardingMembershipRejected";
        }

        return null;
    }

    public static bool CanContinue(RevisionImage image, Guid nodeId, Guid actorId, long actorRevision, DateTimeOffset now)
    {
        OnboardingAttempt? attempt = Find(image, nodeId)?.Attempt;
        if (attempt is null)
        {
            return true;
        }

        bool ongoing = attempt.Status is OnboardingAttemptStatus.Admitted or OnboardingAttemptStatus.MembershipAccepted;
        return ongoing && attempt.Delegation.ActorId == actorId && attempt.Delegation.ActorRevision == actorRevision
            && OnboardingReducer.Active(image, actorId, actorRevision) && now - attempt.LastProgressAtUtc < TimeSpan.FromHours(24);
    }

    public static RevisionImage Confirm(RevisionImage image, Guid nodeId, bool removed)
    {
        OnboardingInvitation? invitation = Find(image, nodeId);
        if (invitation?.Attempt is null || removed)
        {
            return image;
        }

        OnboardingAttempt attempt = invitation.Attempt;
        if (attempt.Status != OnboardingAttemptStatus.Admitted)
        {
            throw new InvalidDataException("OnboardingMembershipStateMismatch");
        }

        attempt = attempt with { Status = InvitationTransitions.Advance(attempt.Status) };
        RevisionImage result = OnboardingReducer.Put(image, invitation with { Attempt = attempt });
        return result with { Onboarding = result.Onboarding! with { Revision = image.Index } };
    }

    public static OnboardingInvitation? Find(RevisionImage image, Guid nodeId)
    {
        return image.Onboarding?.Invitations.Values.SingleOrDefault(item => item.Attempt?.Request.NodeId == nodeId);
    }
}
