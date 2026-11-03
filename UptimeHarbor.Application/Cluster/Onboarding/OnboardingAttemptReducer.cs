using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Onboarding;

/// <summary>Ограниченные продолжения исходной попытки; poll не обновляет срок прогресса.</summary>
public static class OnboardingAttemptReducer
{
    public static string? Apply(RevisionImage image, RevisionCommand command, OnboardingInvitation invitation,
        DateTimeOffset now, out RevisionImage result, out string? reason)
    {
        result = image;
        reason = null;
        OnboardingCommand data = command.Onboarding!;
        OnboardingAttempt? attempt = invitation.Attempt;
        if (attempt is null || attempt.Request.AttemptId != data.AttemptId || InvitationTransitions.IsTerminal(attempt.Status))
        {
            return "OnboardingAttemptUnavailable";
        }

        if (command.Action == "CancelOnboardingAttempt")
        {
            bool membership = image.Operations.Nodes.GetValueOrDefault(attempt.Request.NodeId)?.Voting == true;
            bool pendingMembership = image.Operations.Changes.Values.Any(change => change.NodeId == attempt.Request.NodeId
                && !change.ConfirmedVersion.HasValue);
            if (membership || pendingMembership)
            {
                reason = "MembershipPreservedRemovalRequired";
                return null;
            }

            attempt = attempt with { Status = OnboardingAttemptStatus.Cancelled, ResumeStatus = null, SafeReason = "AdministratorCancelled" };
            InvitationStatus status = invitation.Status;
            if (status == InvitationStatus.Reserved)
            {
                status = InvitationTransitions.Change(status, InvitationStatus.Revoked);
            }

            result = OnboardingReducer.Put(image, invitation with { Attempt = attempt, Status = status });
            if (attempt.AdmitOperationId.HasValue)
            {
                ClusterOperationsImage operations = result.Operations;
                foreach (OperationalCredential credential in operations.Credentials.Values.Where(item => item.AdmissionId == attempt.AdmitOperationId))
                {
                    operations = operations with { Credentials = operations.Credentials.SetItem(credential.Id, credential with { RevokedAtUtc = now }) };
                }

                OperationalNode node = operations.Nodes[attempt.Request.NodeId];
                operations = operations with { Nodes = operations.Nodes.SetItem(node.NodeId,
                    node with { Removed = true, Activated = false, Revision = image.Index }) };

                result = result with { Operations = operations with { Revision = image.Index } };
            }

            return null;
        }

        if (command.Action == "ContinueOnboardingAttempt")
        {
            if (attempt.Status != OnboardingAttemptStatus.PauseAwaitingAdministrator || !attempt.ResumeStatus.HasValue)
            {
                return "OnboardingContinueUnavailable";
            }

            OnboardingDelegation delegation = new(command.ActorUserId, command.ActorRevision, image.Index, data.ReauthenticatedAtUtc!.Value);
            attempt = attempt with
            {
                Status = attempt.ResumeStatus.Value, ResumeStatus = null, Delegation = delegation,
                ReservationUntilUtc = now.AddMinutes(30), LastProgressAtUtc = now, SafeReason = null
            };
            result = OnboardingReducer.Put(image, invitation with { Attempt = attempt });
            return null;
        }

        bool delegationChanged = !OnboardingReducer.Active(image, attempt.Delegation.ActorId, attempt.Delegation.ActorRevision);
        bool inactive = now - attempt.LastProgressAtUtc >= TimeSpan.FromHours(24);
        bool reservationExpired = !attempt.AdmitOperationId.HasValue && now >= attempt.ReservationUntilUtc;
        if (delegationChanged || inactive || reservationExpired || command.Action == "PauseOnboardingAttempt")
        {
            if (attempt.Status != OnboardingAttemptStatus.PauseAwaitingAdministrator)
            {
                string safeReason = "AdministratorRequested";
                if (delegationChanged)
                {
                    safeReason = "DelegationChanged";
                }
                else if (inactive)
                {
                    safeReason = "InactivityExpired";
                }
                else if (reservationExpired)
                {
                    safeReason = "ReservationExpired";
                }

                attempt = attempt with
                {
                    ResumeStatus = attempt.Status, Status = OnboardingAttemptStatus.PauseAwaitingAdministrator, SafeReason = safeReason
                };
                result = OnboardingReducer.Put(image, invitation with { Attempt = attempt });
            }

            if (command.Action == "PauseOnboardingAttempt")
            {
                return null;
            }

            return "OnboardingAdministratorRequired";
        }

        if (attempt.Status == OnboardingAttemptStatus.PauseAwaitingAdministrator)
        {
            return "OnboardingAdministratorRequired";
        }

        if (command.Schema == 3 && attempt.Issuance is not null)
        {
            AcceptedEnrollmentIssuer? issuer = image.OnboardingContracts?.Issuers?.GetValueOrDefault(attempt.Issuance.IssuerEpoch);
            OperationalNode? owner = IssuerOwner(image, issuer);
            if (issuer is null || issuer.RevokedAtUtc.HasValue || owner is null || owner.Removed
                || owner.StorageGeneration != issuer.StorageGeneration)
            {
                return "EnrollmentIssuerUnavailable";
            }

            if (attempt.AdmitOperationId.HasValue)
            {
                OperationalNode? node = image.Operations.Nodes.GetValueOrDefault(attempt.Request.NodeId);
                bool credential = image.Operations.Credentials.Values.Any(item => item.AdmissionId == attempt.AdmitOperationId
                    && item.NodeId == attempt.Request.NodeId && item.Fingerprint == attempt.CertificateDigest
                    && !item.RevokedAtUtc.HasValue && now < item.ExpiresAtUtc);
                if (node is null || node.Removed || node.StorageGeneration != attempt.Request.StorageGeneration || !credential)
                {
                    return "OnboardingCredentialUnavailable";
                }
            }
        }

        if (command.Action == "ResumeOnboardingAttempt")
        {
            if (data.BootId == attempt.BootId || data.ProcessEpoch != attempt.ProcessEpoch + 1 || now < attempt.ProcessLeaseUntilUtc
                || command.Schema == 3 && data.BootChallenge == attempt.BootChallenge)
            {
                return "OnboardingProcessLeaseActive";
            }

            attempt = attempt with
            {
                BootId = data.BootId, BootChallenge = data.BootChallenge, ProcessEpoch = data.ProcessEpoch,
                ProcessLeaseUntilUtc = now.AddMinutes(2)
            };
            result = OnboardingReducer.Put(image, invitation with { Attempt = attempt });
            return null;
        }

        if (data.BootId != attempt.BootId || data.ProcessEpoch != attempt.ProcessEpoch
            || command.Schema == 3 && data.BootChallenge != attempt.BootChallenge)
        {
            return "OnboardingProcessChanged";
        }

        if (command.Action == "RenewOnboardingProcess")
        {
            attempt = attempt with { ProcessLeaseUntilUtc = now.AddMinutes(2) };
            result = OnboardingReducer.Put(image, invitation with { Attempt = attempt });
            return null;
        }

        if (now >= attempt.ProcessLeaseUntilUtc)
        {
            return "OnboardingProcessChanged";
        }

        return Advance(image, command, invitation, attempt, now, out result);
    }

    private static string? Advance(RevisionImage image, RevisionCommand command, OnboardingInvitation invitation,
        OnboardingAttempt attempt, DateTimeOffset now, out RevisionImage result)
    {
        result = image;
        OnboardingCommand data = command.Onboarding!;
        switch (command.Action)
        {
            case "AuthorizeEnrollmentCertificate":
                EnrollmentIssuance template = data.Issuance!;
                DateTimeOffset templateTime = now;
                if (command.Schema == 3)
                {
                    // Immutable intent сохраняется до majority/network: перенос через секундную границу
                    // не меняет template/digest. Допускается только уже начавшийся срок, не его продление.
                    templateTime = template.NotAfterUtc.AddDays(-30);
                    if (templateTime > now || now - templateTime > TimeSpan.FromSeconds(10))
                    {
                        return "EnrollmentTemplateIntentExpired";
                    }
                }

                if (attempt.Status != OnboardingAttemptStatus.Reserved || !ValidTemplate(attempt.Request, template, templateTime))
                {
                    return "EnrollmentIssuanceUnavailable";
                }

                if (command.Schema == 3)
                {
                    AcceptedEnrollmentIssuer? issuer = image.OnboardingContracts?.Issuers?.GetValueOrDefault(template.IssuerEpoch);
                    OperationalNode? owner = IssuerOwner(image, issuer);
                    if (issuer is null || issuer.RetiredAtUtc.HasValue || issuer.RevokedAtUtc.HasValue
                        || owner is null || owner.Removed || !owner.Voting || owner.StorageGeneration != issuer.StorageGeneration
                        || issuer.NotBeforeUtc > template.NotBeforeUtc || issuer.NotAfterUtc < template.NotAfterUtc
                        || template.NotBeforeUtc.Ticks % TimeSpan.TicksPerSecond != 0)
                    {
                        return "EnrollmentIssuerUnavailable";
                    }
                }

                attempt = attempt with
                {
                    Status = InvitationTransitions.Advance(attempt.Status), IssueId = data.IssueId, Issuance = data.Issuance
                };
                if (command.Schema == 3)
                {
                    attempt = attempt with { IssuanceAuthorizationIndex = image.Index };
                }

                break;
            case "BindEnrollmentCredential":
                if (attempt.Status != OnboardingAttemptStatus.CertificateAuthorized || attempt.IssueId != data.IssueId)
                {
                    return "EnrollmentBindingUnavailable";
                }

                attempt = attempt with { Status = InvitationTransitions.Advance(attempt.Status), CertificateDigest = data.CertificateDigest };
                break;
            case "AdmitInvitedNode":
                return OnboardingAdmission.Admit(image, command, invitation, attempt, now, out result);
            case "RequestOnboardingMembership":
            case "AuthorizeOnboardingMembership":
            case "ConfirmOnboardingMembership":
                return OnboardingMembership.Apply(image, command, attempt, now, out result);
            case "CompleteOnboardingAttempt":
                OperationalNode? node = image.Operations.Nodes.GetValueOrDefault(attempt.Request.NodeId);
                if (attempt.Status != OnboardingAttemptStatus.MembershipAccepted || node is null || !node.Voting || node.Removed || !node.Activated)
                {
                    return "OnboardingCompletionUnavailable";
                }

                attempt = attempt with { Status = InvitationTransitions.Advance(attempt.Status) };
                break;
            case "RenewOnboardingAdmission":
                if (attempt.Status != OnboardingAttemptStatus.Admitted || !attempt.AdmissionUntilUtc.HasValue)
                {
                    return "OnboardingAdmissionUnavailable";
                }

                if (attempt.AdmissionUntilUtc.Value - now >= TimeSpan.FromMinutes(3))
                {
                    return "OnboardingRenewalTooEarly";
                }

                return OnboardingAdmission.Renew(image, invitation, attempt, now, out result);
            case "RecordOnboardingProgress":
                if (data.ProgressIndex <= attempt.ProgressIndex || data.ProgressIndex > image.Index)
                {
                    return "OnboardingProgressUnchanged";
                }

                attempt = attempt with { ProgressIndex = data.ProgressIndex, LastProgressAtUtc = now };
                break;
            default:
                return "OnboardingStepUnavailable";
        }

        attempt = attempt with { ProcessLeaseUntilUtc = now.AddMinutes(2) };
        result = OnboardingReducer.Put(image, invitation with { Attempt = attempt });
        return null;
    }

    private static bool ValidTemplate(EnrollmentRequest request, EnrollmentIssuance issuance, DateTimeOffset now)
    {
        bool identity = issuance.NodeId == request.NodeId && issuance.StorageGeneration == request.StorageGeneration
            && issuance.PeerUri == request.PeerUri && issuance.SpkiDigest == request.SpkiDigest && issuance.CsrDigest == request.CsrDigest;
        return identity && issuance.NotBeforeUtc >= now.AddMinutes(-2) && issuance.NotBeforeUtc <= now
            && issuance.NotAfterUtc == now.AddDays(30);
    }

    private static OperationalNode? IssuerOwner(RevisionImage image, AcceptedEnrollmentIssuer? issuer)
    {
        if (issuer is null)
        {
            return null;
        }

        return image.Operations.Nodes.GetValueOrDefault(issuer.NodeId);
    }
}
