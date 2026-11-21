using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

public static class VoterCredentialArtifactValidation
{
    public static void Require(RevisionImage image, VoterCredentialSigningRequest call, EnrollmentIssuedArtifact artifact,
        DateTimeOffset now, VoterCredentialLegacyTrust? legacy = null)
    {
        Require(image, call, artifact, now, legacy, false);
    }

    internal static void Require(RevisionImage image, VoterCredentialSigningRequest call, EnrollmentIssuedArtifact artifact,
        DateTimeOffset now, VoterCredentialLegacyTrust? legacy, bool exactBoundReplay)
    {
        VoterCredentialRotation rotation = VoterCredentialAuthorityPolicy.RequireAccepted(image, call, now, legacy, exactBoundReplay);
        VoterCredentialRequest request = call.ProofRequest.Request;
        RequireIssued(image, request, artifact, VoterCredentialProofs.Digest(request), rotation.AuthorizedIndex, now);
    }

    internal static void RequireIssued(RevisionImage image, VoterCredentialRequest request, EnrollmentIssuedArtifact artifact,
        string requestDigest, long acceptedIndex, DateTimeOffset now)
    {
        EnrollmentIssuance template = request.Renewal.Issuance;
        AcceptedEnrollmentIssuer issuer = EnrollmentPeerAuthority.RequireIssuer(image, template.IssuerEpoch, now);
        RequireIssued(issuer, request, artifact, requestDigest, acceptedIndex, now);
    }

    internal static void RequireIssued(AcceptedEnrollmentIssuer issuer, VoterCredentialRequest request, EnrollmentIssuedArtifact artifact,
        string requestDigest, long acceptedIndex, DateTimeOffset now)
    {
        EnrollmentIssuance template = request.Renewal.Issuance;
        if (artifact.IssueId != template.IssueId || artifact.IssuerEpoch != template.IssuerEpoch
            || artifact.AttemptId != request.OperationId || artifact.GenesisId.HasValue
            || artifact.AuthorizationIndex != acceptedIndex
            || artifact.RequestDigest != requestDigest
            || artifact.TemplateDigest != EnrollmentSigningJournal.TemplateDigest(template)
            || artifact.CertificateDer.Length is < 1 or > EnrollmentCertificates.MAX_DER_BYTES
            || artifact.CertificateDigest != EnrollmentCertificates.Hash(artifact.CertificateDer))
        {
            throw new UnauthorizedAccessException("VoterCredentialSignerResponseInvalid");
        }

        using X509Certificate2 root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(issuer.CertificateDer));
        using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(artifact.CertificateDer);
        if (EnrollmentCertificates.Hash(root.RawData) != issuer.CertificateDigest
            || EnrollmentCertificates.Hash(root.PublicKey.ExportSubjectPublicKeyInfo()) != issuer.SpkiDigest
            || !EnrollmentCertificates.ValidateLeaf(leaf, root, VoterCredentialProofs.CertificateIdentity(request), template,
                artifact.CertificateDigest, now, true))
        {
            throw new UnauthorizedAccessException("VoterCredentialSignerCertificateInvalid");
        }
    }
}
