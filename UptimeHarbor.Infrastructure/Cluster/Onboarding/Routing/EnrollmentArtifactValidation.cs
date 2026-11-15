using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;

/// <summary>Проверяет полученный DER независимо от доверия к authenticated transport.</summary>
public static class EnrollmentArtifactValidation
{
    public static void Require(RevisionImage image, EnrollmentRequest request, EnrollmentIssuance template,
        EnrollmentIssuedArtifact artifact, DateTimeOffset now)
    {
        OnboardingAttempt? attempt = image.Onboarding?.Invitations.GetValueOrDefault(request.InvitationId)?.Attempt;
        AcceptedEnrollmentIssuer? issuer = image.OnboardingContracts?.Issuers?.GetValueOrDefault(template.IssuerEpoch);
        bool exact = attempt is not null && attempt.Request == request && attempt.Issuance == template
            && artifact.AuthorizationIndex == attempt.IssuanceAuthorizationIndex && artifact.AuthorizationIndex > 0
            && artifact.AuthorizationIndex <= image.Index && artifact.GenesisId is null
            && artifact.IssueId == template.IssueId && artifact.IssuerEpoch == template.IssuerEpoch
            && artifact.AttemptId == request.AttemptId && artifact.RequestDigest == EnrollmentRequestCodec.Digest(request)
            && artifact.TemplateDigest == EnrollmentSigningJournal.TemplateDigest(template)
            && artifact.CertificateDer.Length is > 0 and <= 65536
            && artifact.CertificateDigest == EnrollmentCertificates.Hash(artifact.CertificateDer);
        if (!exact || issuer is null || issuer.RevokedAtUtc.HasValue || issuer.RetiredAtUtc.HasValue
            || now >= attempt!.ProcessLeaseUntilUtc || !OnboardingReducer.Active(image, attempt.Delegation.ActorId, attempt.Delegation.ActorRevision)
            || attempt.CertificateDigest is not null && attempt.CertificateDigest != artifact.CertificateDigest)
        {
            throw new UnauthorizedAccessException("EnrollmentArtifactBindingInvalid");
        }

        using X509Certificate2 root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(issuer.CertificateDer));
        using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(artifact.CertificateDer);
        if (issuer.CertificateDigest != EnrollmentCertificates.Hash(root.RawData)
            || !EnrollmentCertificates.ValidateLeaf(leaf, root, request, template, artifact.CertificateDigest, now, true))
        {
            throw new UnauthorizedAccessException("EnrollmentArtifactCertificateInvalid");
        }
    }
}
