using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Onboarding;

/// <summary>Потребление invitation и создание nonvoter входят в один существующий write set.</summary>
public static class OnboardingAdmission
{
    public static string? Admit(RevisionImage image, RevisionCommand command, OnboardingInvitation invitation,
        OnboardingAttempt attempt, DateTimeOffset now, out RevisionImage result)
    {
        result = image;
        OperationalNode node = command.Onboarding!.Node!;
        OperationalCredential credential = command.Onboarding.Credential!;
        EnrollmentRequest request = attempt.Request;
        bool identity = node.NodeId == request.NodeId && node.StorageGeneration == request.StorageGeneration
            && node.Address == request.PeerUri && credential.NodeId == request.NodeId;
        bool bound = credential.Fingerprint == attempt.CertificateDigest && credential.AdmissionId == command.OperationId
            && !credential.AdmissionExpiresAtUtc.HasValue && credential.ExpiresAtUtc == attempt.Issuance?.NotAfterUtc;
        bool fresh = !node.Voting && !node.Activated && !node.Removed && node.Revision == 0
            && !string.IsNullOrWhiteSpace(node.RaftMemberId) && !credential.RevokedAtUtc.HasValue && !credential.ConsumedAtUtc.HasValue;
        if (invitation.Status != InvitationStatus.Reserved || attempt.Status != OnboardingAttemptStatus.CredentialBound
            || !identity || !bound || !fresh || credential.ExpiresAtUtc <= now.AddMinutes(10))
        {
            return "OnboardingAdmissionMismatch";
        }

        StorageGuards.Id(credential.Id, nameof(credential.Id));
        StorageGuards.PublicCertificate(credential.PublicCertificate);
        credential = credential with { AdmissionExpiresAtUtc = now.AddMinutes(10) };
        RevisionCommand admission = command with { Operation = new ClusterOperation { Node = node, Credential = credential } };
        string? error = ClusterOperationReducer.AdmitAccepted(image, admission, out result);
        if (error is not null)
        {
            return error;
        }

        attempt = attempt with
        {
            Status = InvitationTransitions.Advance(attempt.Status), AdmitOperationId = command.OperationId,
            AdmissionUntilUtc = now.AddMinutes(10), ProcessLeaseUntilUtc = now.AddMinutes(2), LastProgressAtUtc = now
        };
        result = result with { Operations = result.Operations with { Revision = image.Index } };
        result = OnboardingReducer.Put(result, invitation with
        {
            Status = InvitationTransitions.Change(invitation.Status, InvitationStatus.Consumed), Attempt = attempt
        });
        return null;
    }

    public static string? Renew(RevisionImage image, OnboardingInvitation invitation, OnboardingAttempt attempt,
        DateTimeOffset now, out RevisionImage result)
    {
        result = image;
        OperationalNode? node = image.Operations.Nodes.GetValueOrDefault(attempt.Request.NodeId);
        OperationalCredential? credential = image.Operations.Credentials.Values.SingleOrDefault(item => item.AdmissionId == attempt.AdmitOperationId);
        if (node is null || node.Voting || node.Removed || credential is null || credential.RevokedAtUtc.HasValue
            || credential.ConsumedAtUtc.HasValue || credential.ExpiresAtUtc <= now.AddMinutes(10))
        {
            return "OnboardingAdmissionUnavailable";
        }

        credential = credential with { AdmissionExpiresAtUtc = now.AddMinutes(10) };
        result = image with { Operations = image.Operations with
        {
            Revision = image.Index, Credentials = image.Operations.Credentials.SetItem(credential.Id, credential)
        } };
        attempt = attempt with { AdmissionUntilUtc = now.AddMinutes(10), ProcessLeaseUntilUtc = now.AddMinutes(2) };
        result = OnboardingReducer.Put(result, invitation with { Attempt = attempt });
        return null;
    }
}
