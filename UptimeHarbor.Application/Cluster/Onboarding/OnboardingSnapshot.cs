using System.Text;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Onboarding;

public static class OnboardingSnapshot
{
    public static IReadOnlyList<ProjectionRecord> Changes(RevisionImage before, RevisionImage after)
    {
        List<ProjectionRecord> records = [];
        if (after.Onboarding is null)
        {
            return records;
        }

        JournalPosition position = new(new ClusterIdentity(after.ClusterId, after.Generation), after.Index, after.Term);
        foreach (OnboardingInvitation invitation in after.Onboarding.Invitations.Values)
        {
            if (before.Onboarding?.Invitations.GetValueOrDefault(invitation.Id) == invitation)
            {
                continue;
            }

            string payload = Encoding.UTF8.GetString(StrictOnboardingJson.Encode(invitation));
            records.Add(new OnboardingProjection(invitation.Id, position, invitation.Status, payload));
        }

        return records;
    }

    public static void Validate(RevisionImage image)
    {
        if (image.RecoveryBaseline is not null)
        {
            ValidateBaseline(image.RecoveryBaseline, image);
        }

        if (image.Onboarding is not null)
        {
            OnboardingImage onboarding = image.Onboarding;
            if (onboarding.Version != 1 || onboarding.Revision <= 0 || onboarding.Revision > image.Index
                || onboarding.Invitations is null || onboarding.Requests is null)
            {
                throw new InvalidDataException("OnboardingSnapshotVersionInvalid");
            }

            foreach (KeyValuePair<Guid, OnboardingRequestReceipt> item in onboarding.Requests)
            {
                OnboardingRequestReceipt receipt = item.Value;
                if (receipt is null || item.Key != receipt.OperationId || !image.Results.TryGetValue(item.Key, out CommandResult? decision)
                    || decision.Digest != receipt.Digest || decision.Index > image.Index)
                {
                    throw new InvalidDataException("OnboardingRequestSnapshotMismatch");
                }
            }

            HashSet<Guid> attempts = [];
            foreach (KeyValuePair<Guid, OnboardingInvitation> item in onboarding.Invitations)
            {
                OnboardingInvitation invitation = item.Value;
                if (invitation is null || invitation.Delegation is null)
                {
                    throw new InvalidDataException("OnboardingInvitationSnapshotInvalid");
                }

                StorageGuards.Id(invitation.Id, nameof(invitation.Id));
                StorageGuards.Id(invitation.IssueOperationId, nameof(invitation.IssueOperationId));
                StorageGuards.Digest(invitation.Verifier);
                StorageGuards.Utc(invitation.IssuedAtUtc);
                StorageGuards.Utc(invitation.ExpiresAtUtc);
                Delegation(invitation.Delegation, image.Index);
                if (item.Key != invitation.Id || !Enum.IsDefined(invitation.Status)
                    || invitation.ExpiresAtUtc != invitation.IssuedAtUtc.AddMinutes(15))
                {
                    throw new InvalidDataException("OnboardingInvitationSnapshotInvalid");
                }

                OnboardingAttempt? attempt = invitation.Attempt;
                if (attempt is null)
                {
                    if (invitation.Status is InvitationStatus.Reserved or InvitationStatus.Consumed)
                    {
                        throw new InvalidDataException("OnboardingAttemptSnapshotMissing");
                    }

                    continue;
                }

                ValidateAttempt(image, invitation, attempt);
                if (!attempts.Add(attempt.Request.AttemptId))
                {
                    throw new InvalidDataException("OnboardingAttemptSnapshotDuplicate");
                }
            }

            int unused = onboarding.Invitations.Values.Count(item => item.Status == InvitationStatus.Unused);
            int active = onboarding.Invitations.Values.Count(item => item.Attempt is not null && !InvitationTransitions.IsTerminal(item.Attempt.Status));
            if (unused > 1 || active > 1)
            {
                throw new InvalidDataException("OnboardingSnapshotLimitsInvalid");
            }
        }

        OnboardingContractSnapshot.Validate(image);
    }

    private static void ValidateBaseline(RecoveryBaseline baseline, RevisionImage image)
    {
        RevisionImage history = new(baseline.PreviousClusterId, baseline.PreviousGeneration)
        {
            Index = baseline.SourceIndex, Term = baseline.SourceTerm, Operations = baseline.PreviousOperations,
            Onboarding = baseline.PreviousOnboarding, OnboardingContracts = baseline.PreviousOnboardingContracts,
            Results = image.Results
        };
        Validate(history);
        foreach (RecoveryBaseline ancestor in baseline.Ancestors)
        {
            ValidateBaseline(ancestor, image);
        }
    }

    private static void ValidateAttempt(RevisionImage image, OnboardingInvitation invitation, OnboardingAttempt attempt)
    {
        if (attempt.Request is null || attempt.Delegation is null)
        {
            throw new InvalidDataException("OnboardingAttemptSnapshotInvalid");
        }

        EnrollmentRequestCodec.Validate(attempt.Request);
        Delegation(attempt.Delegation, image.Index);
        StorageGuards.Id(attempt.ReserveOperationId, nameof(attempt.ReserveOperationId));
        StorageGuards.Id(attempt.BootId, nameof(attempt.BootId));
        StorageGuards.Utc(attempt.ReservationUntilUtc);
        StorageGuards.Utc(attempt.ProcessLeaseUntilUtc);
        StorageGuards.Utc(attempt.LastProgressAtUtc);
        if (attempt.BootChallenge is not null && !OnboardingCommands.IsBootChallenge(attempt.BootChallenge))
        {
            throw new InvalidDataException("OnboardingBootChallengeSnapshotInvalid");
        }
        bool identity = attempt.Request.ClusterId == image.ClusterId && attempt.Request.Generation == image.Generation
            && attempt.Request.InvitationId == invitation.Id;
        bool paused = attempt.Status == OnboardingAttemptStatus.PauseAwaitingAdministrator;
        if (!identity || attempt.ProcessEpoch < 1 || attempt.ProgressIndex < 0 || attempt.ProgressIndex > image.Index
            || EnrollmentRequestCodec.Digest(attempt.Request) != attempt.RequestDigest || !Enum.IsDefined(attempt.Status)
            || paused != attempt.ResumeStatus.HasValue
            || attempt.ResumeStatus.HasValue && (!Enum.IsDefined(attempt.ResumeStatus.Value)
                || attempt.ResumeStatus == OnboardingAttemptStatus.PauseAwaitingAdministrator || InvitationTransitions.IsTerminal(attempt.ResumeStatus.Value))
            || (attempt.AdmitOperationId.HasValue != (invitation.Status == InvitationStatus.Consumed)))
        {
            throw new InvalidDataException("OnboardingAttemptSnapshotInvalid");
        }

        OnboardingAttemptStatus stage = attempt.ResumeStatus ?? attempt.Status;
        bool needsIssue = stage is OnboardingAttemptStatus.CertificateAuthorized or OnboardingAttemptStatus.CredentialBound
            or OnboardingAttemptStatus.Admitted or OnboardingAttemptStatus.MembershipAccepted or OnboardingAttemptStatus.Completed;
        bool needsBinding = stage is OnboardingAttemptStatus.CredentialBound or OnboardingAttemptStatus.Admitted
            or OnboardingAttemptStatus.MembershipAccepted or OnboardingAttemptStatus.Completed;
        bool admitted = stage is OnboardingAttemptStatus.Admitted or OnboardingAttemptStatus.MembershipAccepted or OnboardingAttemptStatus.Completed;
        if (attempt.IssueId.HasValue != (attempt.Issuance is not null) || needsIssue && !attempt.IssueId.HasValue
            || needsBinding && attempt.CertificateDigest is null || admitted && !attempt.AdmitOperationId.HasValue
            || attempt.CertificateDigest is not null && !attempt.IssueId.HasValue)
        {
            throw new InvalidDataException("OnboardingAttemptStageMismatch");
        }

        if (attempt.CertificateDigest is not null)
        {
            StorageGuards.Digest(attempt.CertificateDigest);
        }

        if (attempt.Issuance is EnrollmentIssuance issuance)
        {
            if (attempt.BootChallenge is not null && (attempt.IssuanceAuthorizationIndex <= 0
                || attempt.IssuanceAuthorizationIndex > image.Index
                || !image.Results.Values.Any(result => result.Index == attempt.IssuanceAuthorizationIndex
                    && result.Outcome == OperationOutcome.Committed
                    && image.Onboarding!.Requests.GetValueOrDefault(result.OperationId) is OnboardingRequestReceipt receipt
                    && receipt.Action == "AuthorizeEnrollmentCertificate" && receipt.AttemptId == attempt.Request.AttemptId
                    && receipt.InvitationId == attempt.Request.InvitationId)))
            {
                throw new InvalidDataException("OnboardingIssuanceAuthorizationSnapshotInvalid");
            }

            StorageGuards.Utc(issuance.NotBeforeUtc);
            StorageGuards.Utc(issuance.NotAfterUtc);
            if (issuance.Version != 1 || issuance.IssueId != attempt.IssueId || issuance.IssuerEpoch == Guid.Empty
                || issuance.NodeId != attempt.Request.NodeId || issuance.StorageGeneration != attempt.Request.StorageGeneration
                || issuance.PeerUri != attempt.Request.PeerUri || issuance.SpkiDigest != attempt.Request.SpkiDigest
                || issuance.CsrDigest != attempt.Request.CsrDigest || issuance.NotAfterUtc <= issuance.NotBeforeUtc)
            {
                throw new InvalidDataException("OnboardingIssuanceSnapshotMismatch");
            }
        }

        if (attempt.AdmitOperationId.HasValue)
        {
            OperationalNode? node = image.Operations.Nodes.GetValueOrDefault(attempt.Request.NodeId);
            if (node is null || node.StorageGeneration != attempt.Request.StorageGeneration || node.Address != attempt.Request.PeerUri
                || !attempt.AdmissionUntilUtc.HasValue || attempt.CertificateDigest is null)
            {
                throw new InvalidDataException("OnboardingAdmissionSnapshotInvalid");
            }
        }
    }

    private static void Delegation(OnboardingDelegation delegation, long index)
    {
        StorageGuards.Id(delegation.ActorId, nameof(delegation.ActorId));
        StorageGuards.Utc(delegation.ReauthenticatedAtUtc);
        if (delegation.ActorRevision <= 0 || delegation.Revision <= 0 || delegation.Revision > index)
        {
            throw new InvalidDataException("OnboardingDelegationInvalid");
        }
    }
}
