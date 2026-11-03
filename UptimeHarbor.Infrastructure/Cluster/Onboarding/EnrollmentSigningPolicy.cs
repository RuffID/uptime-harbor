using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Проверяет exact accepted binding на уже подтверждённом majority срезе, не создаёт сам срез.</summary>
public static class EnrollmentSigningPolicy
{
    public static long Require(RevisionImage image, EnrollmentRequest request, EnrollmentIssuance issuance,
        EnrollmentIssuerReference localIssuer, Guid localNodeId, Guid localStorage, DateTimeOffset now)
    {
        OnboardingInvitation? invitation = image.Onboarding?.Invitations.GetValueOrDefault(request.InvitationId);
        OnboardingAttempt? attempt = invitation?.Attempt;
        AcceptedEnrollmentIssuer? issuer = image.OnboardingContracts?.Issuers?.GetValueOrDefault(issuance.IssuerEpoch);
        OperationalNode? owner = image.Operations.Nodes.GetValueOrDefault(localNodeId);
        if (image.ClusterId != request.ClusterId || image.Generation != request.Generation || image.Operations.MaintenanceId != Guid.Empty
            || attempt is null || attempt.Request != request || attempt.RequestDigest != EnrollmentRequestCodec.Digest(request)
            || attempt.Issuance != issuance || attempt.IssueId != issuance.IssueId || attempt.IssuanceAuthorizationIndex <= 0
            || attempt.IssuanceAuthorizationIndex > image.Index || !OnboardingCommands.IsBootChallenge(attempt.BootChallenge)
            || invitation!.Status is not (InvitationStatus.Reserved or InvitationStatus.Consumed)
            || attempt.Status is not (OnboardingAttemptStatus.CertificateAuthorized or OnboardingAttemptStatus.CredentialBound
                or OnboardingAttemptStatus.Admitted or OnboardingAttemptStatus.MembershipAccepted)
            || now >= attempt.ProcessLeaseUntilUtc || now - attempt.LastProgressAtUtc >= TimeSpan.FromHours(24)
            || !attempt.AdmitOperationId.HasValue && now >= attempt.ReservationUntilUtc
            || !OnboardingReducer.Active(image, attempt.Delegation.ActorId, attempt.Delegation.ActorRevision)
            || issuer is null || issuer.RetiredAtUtc.HasValue || issuer.RevokedAtUtc.HasValue
            || issuer.NodeId != localNodeId || issuer.StorageGeneration != localStorage
            || owner is null || owner.Removed || !owner.Voting || owner.StorageGeneration != localStorage
            || localIssuer.Epoch != issuer.Epoch || localIssuer.NodeId != issuer.NodeId
            || localIssuer.StorageGeneration != issuer.StorageGeneration || localIssuer.ClusterId != image.ClusterId
            || localIssuer.Generation != image.Generation || localIssuer.CertificateDigest != issuer.CertificateDigest
            || localIssuer.SpkiDigest != issuer.SpkiDigest || Convert.ToBase64String(localIssuer.CertificateDer) != issuer.CertificateDer
            || now < issuer.NotBeforeUtc || now >= issuer.NotAfterUtc
            || issuance.NotBeforeUtc < issuer.NotBeforeUtc || issuance.NotAfterUtc > issuer.NotAfterUtc
            || now >= issuance.NotAfterUtc)
        {
            throw new UnauthorizedAccessException("EnrollmentIssuanceNotCurrent");
        }

        if (attempt.AdmitOperationId.HasValue)
        {
            OperationalNode? node = image.Operations.Nodes.GetValueOrDefault(request.NodeId);
            bool credential = image.Operations.Credentials.Values.Any(item => item.AdmissionId == attempt.AdmitOperationId
                && item.NodeId == request.NodeId && item.Fingerprint == attempt.CertificateDigest
                && !item.RevokedAtUtc.HasValue && now < item.ExpiresAtUtc);
            if (node is null || node.Removed || node.StorageGeneration != request.StorageGeneration || !credential)
            {
                throw new UnauthorizedAccessException("EnrollmentCredentialNotCurrent");
            }
        }

        return attempt.IssuanceAuthorizationIndex;
    }
}
