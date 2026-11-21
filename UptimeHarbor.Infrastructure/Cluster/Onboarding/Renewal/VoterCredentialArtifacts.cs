using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

public static class VoterCredentialArtifacts
{
    public static void ValidateSnapshot(RevisionImage image)
    {
        if (image.RecoveryBaseline is not null)
        {
            ValidateHistory(image.RecoveryBaseline);
        }

        ValidateCurrent(image);
    }

    private static void ValidateHistory(RecoveryBaseline baseline)
    {
        RevisionImage history = new(baseline.PreviousClusterId, baseline.PreviousGeneration)
        {
            Operations = baseline.PreviousOperations, OnboardingContracts = baseline.PreviousOnboardingContracts
        };
        ValidateCurrent(history);
        foreach (RecoveryBaseline ancestor in baseline.Ancestors)
        {
            ValidateHistory(ancestor);
        }
    }

    private static void ValidateCurrent(RevisionImage image)
    {
        foreach (VoterCredentialRotation rotation in image.OnboardingContracts?.VoterCredentialRotations?.Values ?? [])
        {
            VoterCredentialRenewal renewal = rotation.Renewal;
            OperationalCredential old = image.Operations.Credentials[renewal.PreviousCredentialId];
            using X509Certificate2 previous = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(old.PublicCertificate));
            if (EnrollmentCertificates.Hash(previous.RawData) != renewal.PreviousFingerprint
                || EnrollmentCertificates.Hash(previous.PublicKey.ExportSubjectPublicKeyInfo()) != renewal.PreviousSpkiDigest
                || new DateTimeOffset(previous.NotAfter.ToUniversalTime()) != old.ExpiresAtUtc
                || rotation.AuthorizedAtUtc.UtcDateTime < previous.NotBefore.ToUniversalTime())
            {
                throw new InvalidDataException("VoterCredentialPreviousArtifactInvalid");
            }

            if (rotation.CredentialRevision > 0)
            {
                ValidateBound(image, rotation);
            }
        }
    }

    private static void ValidateBound(RevisionImage image, VoterCredentialRotation rotation)
    {
        EnrollmentIssuance template = rotation.Renewal.Issuance;
        OperationalCredential credential = image.Operations.Credentials[template.IssueId];
        AcceptedEnrollmentIssuer issuer = image.OnboardingContracts!.Issuers![template.IssuerEpoch];
        using X509Certificate2 root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(issuer.CertificateDer));
        using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(credential.PublicCertificate));
        // Crypto helper использует только identity/template; эти IDs не создают invitation/attempt.
        EnrollmentRequest request = new(1, image.ClusterId, image.Generation, template.IssueId, template.IssueId,
            template.NodeId, template.StorageGeneration, template.PeerUri, template.SpkiDigest, template.CsrDigest);
        bool issuerExact = EnrollmentCertificates.Hash(root.RawData) == issuer.CertificateDigest
            && EnrollmentCertificates.Hash(root.PublicKey.ExportSubjectPublicKeyInfo()) == issuer.SpkiDigest;
        if (!issuerExact || !EnrollmentCertificates.ValidateLeaf(leaf, root, request, template, credential.Fingerprint,
            rotation.BoundAtUtc!.Value, false))
        {
            throw new InvalidDataException("VoterCredentialBoundArtifactInvalid");
        }
    }
}
