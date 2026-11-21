using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

/// <summary>Только проверка уже подтверждённого среза; самостоятельно majority не доказывает.</summary>
public static class VoterCredentialAuthorityPolicy
{
    public static void RequireCurrent(RevisionImage image, VoterCredentialRequest request, DateTimeOffset now)
    {
        RequireCurrent(image, request, now, false);
    }

    internal static void RequireCurrent(RevisionImage image, VoterCredentialRequest request, DateTimeOffset now,
        bool exactBoundReplay)
    {
        VoterCredentialRenewal renewal = request.Renewal;
        EnrollmentIssuance leaf = renewal.Issuance;
        OperationalNode node = EnrollmentPeerAuthority.RequireVoter(image, leaf.NodeId, leaf.StorageGeneration);
        OperationalCredential? old = image.Operations.Credentials.GetValueOrDefault(renewal.PreviousCredentialId);
        VoterCredentialRotation? latest = VoterCredentialPolicy.Latest(image, leaf.NodeId);
        long revision = latest?.CredentialRevision ?? 0;
        bool currentKey = latest is null || latest.Renewal.Issuance.IssueId == renewal.PreviousCredentialId
            && latest.Renewal.Issuance.SpkiDigest == renewal.PreviousSpkiDigest;
        bool boundReplay = exactBoundReplay && latest is not null && latest.Renewal == renewal
            && latest.AuthorizationOperationId == request.OperationId && !latest.RetiredIndex.HasValue;
        if (image.ClusterId != request.ClusterId || image.Generation != request.Generation || node.Address != leaf.PeerUri
            || old is null || old.NodeId != node.NodeId || old.Fingerprint != renewal.PreviousFingerprint
            || (!currentKey || revision != renewal.PreviousCredentialRevision) && !boundReplay
            || !VoterCredentialPolicy.IsUsable(image, old, now) || old.ExpiresAtUtc - now > TimeSpan.FromDays(7))
        {
            throw new UnauthorizedAccessException("VoterCredentialNotCurrent");
        }

        AcceptedEnrollmentIssuer issuer = EnrollmentPeerAuthority.RequireIssuer(image, leaf.IssuerEpoch, now);
        if (issuer.Purpose != EnrollmentIssuer.PEER_PURPOSE || leaf.NotBeforeUtc < issuer.NotBeforeUtc
            || leaf.NotAfterUtc > issuer.NotAfterUtc || now < leaf.NotBeforeUtc || now >= leaf.NotAfterUtc)
        {
            throw new UnauthorizedAccessException("VoterCredentialIssuerTemplateInvalid");
        }
    }

    public static VoterCredentialRotation RequireAccepted(RevisionImage image, VoterCredentialSigningRequest call,
        DateTimeOffset now, VoterCredentialLegacyTrust? legacy = null)
    {
        return RequireAccepted(image, call, now, legacy, false);
    }

    internal static VoterCredentialRotation RequireAccepted(RevisionImage image, VoterCredentialSigningRequest call,
        DateTimeOffset now, VoterCredentialLegacyTrust? legacy, bool exactBoundReplay)
    {
        VoterCredentialRequest request = call.ProofRequest.Request;
        VoterCredentialProofs.Require(image, call.ProofRequest, now, legacy, exactBoundReplay);
        VoterCredentialRotation? rotation = image.OnboardingContracts?.VoterCredentialRotations?
            .GetValueOrDefault(request.Renewal.Issuance.IssueId);
        CommandResult? result = image.Results.GetValueOrDefault(request.OperationId);
        if (call.Version != 1 || rotation is null || rotation.Renewal != request.Renewal
            || rotation.AuthorizationOperationId != request.OperationId || rotation.AuthorizedIndex != call.AuthorizationIndex
            || rotation.AuthorizedIndex <= 0 || rotation.AuthorizedIndex > image.Index
            || rotation.CredentialRevision != 0 && !exactBoundReplay
            || rotation.AuthorizedAtUtc > now || rotation.RetiredIndex.HasValue
            || rotation.AuthorizationDigest != RevisionCodec.Digest(VoterCredentialAuthorizationCommand.Create(request))
            || result is null || result.OperationId != request.OperationId || result.Outcome != OperationOutcome.Committed
            || result.Digest != rotation.AuthorizationDigest || result.Index != rotation.AuthorizedIndex
            || result.AppliedRevision != rotation.AuthorizedIndex)
        {
            throw new UnauthorizedAccessException("VoterCredentialExactAuthorizationRequired");
        }

        return rotation;
    }

    public static void RequireLocalIssuer(RevisionImage image, EnrollmentIssuerReference local, Guid nodeId,
        Guid storage, Guid epoch, DateTimeOffset now)
    {
        AcceptedEnrollmentIssuer issuer = EnrollmentPeerAuthority.RequireIssuer(image, epoch, now);
        if (issuer.NodeId != nodeId || issuer.StorageGeneration != storage || local.Epoch != epoch
            || local.NodeId != nodeId || local.StorageGeneration != storage || local.ClusterId != image.ClusterId
            || local.Generation != image.Generation || local.CertificateDigest != issuer.CertificateDigest
            || local.SpkiDigest != issuer.SpkiDigest || Convert.ToBase64String(local.CertificateDer) != issuer.CertificateDer)
        {
            throw new UnauthorizedAccessException("VoterCredentialLocalIssuerMismatch");
        }
    }
}
