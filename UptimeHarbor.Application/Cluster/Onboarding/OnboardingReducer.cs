using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Onboarding;

/// <summary>Последовательное принятие приглашений внутри прежнего reducer; без часов и I/O.</summary>
public static class OnboardingReducer
{
    public static RevisionImage Apply(RevisionImage image, RevisionCommand command)
    {
        OnboardingCommand data = command.Onboarding!;
        if (!data.CoordinatorAtUtc.HasValue || data.CoordinatorTerm != image.Term)
        {
            throw new InvalidDataException("OnboardingAcceptanceUnconfirmed");
        }

        DateTimeOffset now = data.CoordinatorAtUtc.Value;
        OperationalNode? origin = image.Operations.Nodes.GetValueOrDefault(command.OriginNodeId);
        if (origin is null || !origin.Voting || origin.Removed || origin.StorageGeneration != data.StorageGeneration)
        {
            return Decide(image, command, "OnboardingOriginNotAdmitted");
        }

        if (!OnboardingCommands.IsCoordinator(command) && !Active(image, command.ActorUserId, command.ActorRevision))
        {
            return Decide(image, command, "ActorNotActive");
        }

        if (data.ReauthenticatedAtUtc.HasValue)
        {
            TimeSpan age = now - data.ReauthenticatedAtUtc.Value;
            if (age < TimeSpan.Zero || age > TimeSpan.FromMinutes(5))
            {
                return Decide(image, command, "ReauthenticationExpired");
            }
        }

        OnboardingImage onboarding = image.Onboarding ?? new();
        OnboardingInvitation? invitation = onboarding.Invitations.GetValueOrDefault(data.InvitationId);
        if (command.Action == "ReserveInvitation" && invitation?.Attempt is not null)
        {
            OnboardingAttempt existing = invitation.Attempt;
            bool same = existing.Request == data.Request && existing.RequestDigest == data.RequestDigest
                && existing.BootId == data.BootId && existing.ProcessEpoch == data.ProcessEpoch;
            if (same && invitation.Status == InvitationStatus.Reserved)
            {
                if (!InvitationSecrets.Matches(invitation.Verifier, data.Verifier!))
                {
                    return Decide(image, command, "InvitationRejected");
                }

                bool current = Active(image, existing.Delegation.ActorId, existing.Delegation.ActorRevision)
                    && now < existing.ReservationUntilUtc && now - existing.LastProgressAtUtc < TimeSpan.FromHours(24)
                    && existing.Status != OnboardingAttemptStatus.PauseAwaitingAdministrator;
                if (!current)
                {
                    OnboardingAttemptReducer.Apply(image, command with { Action = "PauseOnboardingAttempt" }, invitation,
                        now, out image, out _);
                    return Decide(image, command, "OnboardingAdministratorRequired");
                }

                return Decide(image, command, null, "AlreadyReserved");
            }

            return Decide(image, command, "ReservationBoundToAnotherRequest");
        }

        if (command.BaseRevision != onboarding.Revision)
        {
            return Decide(image, command, "OnboardingRevisionChanged");
        }

        string? error;
        string? reason = null;
        if (command.Action is "IssueInvitation" or "ReplaceInvitation")
        {
            error = Issue(image, command, now, out image);
        }
        else if (invitation is null)
        {
            error = "InvitationMissing";
        }
        else
        {
            error = Change(image, command, invitation, now, out image, out reason);
        }

        if (image.Onboarding != onboarding && image.Onboarding is not null)
        {
            image = image with { Onboarding = image.Onboarding with { Revision = image.Index } };
        }

        return Decide(image, command, error, reason);
    }

    private static string? Issue(RevisionImage image, RevisionCommand command, DateTimeOffset now, out RevisionImage result)
    {
        result = image;
        OnboardingCommand data = command.Onboarding!;
        OnboardingImage onboarding = image.Onboarding ?? new();
        if (onboarding.Invitations.ContainsKey(data.InvitationId))
        {
            return "InvitationIdentityUsed";
        }

        if (data.ReplacesInvitationId.HasValue)
        {
            OnboardingInvitation? old = onboarding.Invitations.GetValueOrDefault(data.ReplacesInvitationId.Value);
            if (old is null || old.Status == InvitationStatus.Revoked)
            {
                return "InvitationReplacementUnavailable";
            }

            if (old.Status is InvitationStatus.Unused or InvitationStatus.Reserved)
            {
                old = Revoke(old);
                onboarding = onboarding with { Invitations = onboarding.Invitations.SetItem(old.Id, old) };
            }
        }

        foreach (OnboardingInvitation old in onboarding.Invitations.Values)
        {
            if (old.Status == InvitationStatus.Unused && now >= old.ExpiresAtUtc)
            {
                onboarding = onboarding with { Invitations = onboarding.Invitations.SetItem(old.Id,
                    old with { Status = InvitationTransitions.Change(old.Status, InvitationStatus.Expired) }) };
            }
        }

        if (onboarding.Invitations.Values.Any(item => item.Status is InvitationStatus.Unused or InvitationStatus.Reserved))
        {
            return "ExplicitInvitationReplacementRequired";
        }

        OnboardingDelegation delegation = new(command.ActorUserId, command.ActorRevision, image.Index, data.ReauthenticatedAtUtc!.Value);
        OnboardingInvitation issued = new(data.InvitationId, command.OperationId, data.Verifier!, now, now.AddMinutes(15), delegation)
        {
            ReplacesInvitationId = data.ReplacesInvitationId
        };
        result = image with { Onboarding = onboarding with { Invitations = onboarding.Invitations.Add(issued.Id, issued) } };
        return null;
    }

    private static string? Change(RevisionImage image, RevisionCommand command, OnboardingInvitation invitation,
        DateTimeOffset now, out RevisionImage result, out string? reason)
    {
        result = image;
        reason = null;
        OnboardingCommand data = command.Onboarding!;
        switch (command.Action)
        {
            case "RevokeInvitation":
                if (invitation.Status == InvitationStatus.Consumed)
                {
                    reason = "MembershipPreservedUseCancelAttempt";
                    return null;
                }

                if (invitation.Status is InvitationStatus.Expired or InvitationStatus.Revoked)
                {
                    reason = "AlreadyClosed";
                    return null;
                }

                result = Put(image, Revoke(invitation));
                return null;
            case "ExpireInvitation":
                if (invitation.Status != InvitationStatus.Unused || now < invitation.ExpiresAtUtc)
                {
                    return "InvitationExpiryUnavailable";
                }

                result = Put(image, invitation with { Status = InvitationTransitions.Change(invitation.Status, InvitationStatus.Expired) });
                return null;
            case "ReserveInvitation":
                return Reserve(image, command, invitation, now, out result);
            default:
                return OnboardingAttemptReducer.Apply(image, command, invitation, now, out result, out reason);
        }
    }

    private static string? Reserve(RevisionImage image, RevisionCommand command, OnboardingInvitation invitation,
        DateTimeOffset now, out RevisionImage result)
    {
        result = image;
        OnboardingCommand data = command.Onboarding!;
        if (invitation.Status != InvitationStatus.Unused)
        {
            return "InvitationUnavailable";
        }

        if (now >= invitation.ExpiresAtUtc)
        {
            result = Put(image, invitation with { Status = InvitationTransitions.Change(invitation.Status, InvitationStatus.Expired) });
            return "InvitationExpired";
        }

        if (!Active(image, invitation.Delegation.ActorId, invitation.Delegation.ActorRevision))
        {
            return "DelegationChanged";
        }

        if (!InvitationSecrets.Matches(invitation.Verifier, data.Verifier!))
        {
            return "InvitationRejected";
        }

        EnrollmentRequest request = data.Request!;
        bool activeJoin = image.Onboarding!.Invitations.Values.Any(item => item.Attempt is not null
            && !InvitationTransitions.IsTerminal(item.Attempt.Status));
        bool duplicate = image.Operations.Nodes.Values.Any(node => node.NodeId == request.NodeId
            || node.StorageGeneration == request.StorageGeneration || !node.Removed && node.Address == request.PeerUri);
        bool previouslyBound = image.Onboarding.Invitations.Values.Any(item => item.Attempt is not null
            && (item.Attempt.Request.AttemptId == request.AttemptId || item.Attempt.Request.NodeId == request.NodeId
                || item.Attempt.Request.StorageGeneration == request.StorageGeneration
                || item.Attempt.Request.SpkiDigest == request.SpkiDigest));
        if (activeJoin || duplicate || previouslyBound || data.ProcessEpoch != 1)
        {
            return "OnboardingIdentityUnavailable";
        }

        OnboardingAttempt attempt = new(request, data.RequestDigest!, command.OperationId, now.AddMinutes(30), invitation.Delegation,
            data.BootId, 1, now.AddMinutes(2), now);
        result = Put(image, invitation with
        {
            Status = InvitationTransitions.Change(invitation.Status, InvitationStatus.Reserved), Attempt = attempt
        });
        return null;
    }

    public static bool Active(RevisionImage image, Guid actorId, long revision)
    {
        AcceptedAccount? actor = RevisionAccounts.Find(image, actorId);
        return AcceptedAdministratorAccessPolicy.IsActiveAdministrator(actor) && actor!.Revision == revision;
    }

    public static RevisionImage Put(RevisionImage image, OnboardingInvitation invitation)
    {
        OnboardingImage onboarding = image.Onboarding ?? new();
        return image with { Onboarding = onboarding with { Invitations = onboarding.Invitations.SetItem(invitation.Id, invitation) } };
    }

    private static OnboardingInvitation Revoke(OnboardingInvitation invitation)
    {
        OnboardingAttempt? attempt = invitation.Attempt;
        if (attempt is not null)
        {
            attempt = attempt with { Status = OnboardingAttemptStatus.Cancelled, ResumeStatus = null, SafeReason = "InvitationRevoked" };
        }

        return invitation with { Status = InvitationTransitions.Change(invitation.Status, InvitationStatus.Revoked), Attempt = attempt };
    }

    private static RevisionImage Decide(RevisionImage image, RevisionCommand command, string? error, string? reason = null)
    {
        OperationOutcome outcome = OperationOutcome.Committed;
        if (error is not null)
        {
            outcome = OperationOutcome.Rejected;
            reason = error;
        }

        CommandResult decision = new(command.OperationId, RevisionCodec.Digest(command), outcome,
            image.Index, image.Term, image.Index, reason, null, null, command.ActorRevision);
        OnboardingCommand data = command.Onboarding!;
        OnboardingRequestReceipt receipt = new(command.OperationId, data.InvitationId, data.AttemptId,
            command.ActorUserId, command.Action, data.ReplacesInvitationId, decision.Digest);
        OnboardingImage onboarding = image.Onboarding ?? new();
        onboarding = onboarding with { Requests = onboarding.Requests.Add(receipt.OperationId, receipt), Revision = image.Index };
        return image with { Results = image.Results.Add(decision.OperationId, decision), Onboarding = onboarding };
    }
}
