using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Фиксированный grant domain; не предоставляет API подписи произвольных control commands.</summary>
public static class OnlineGrantSignatures
{
    public static bool Verify(RevisionImage image, OnlineGrant grant, DateTimeOffset now, bool historical = false,
        bool processRenewal = false)
    {
        try
        {
            OnlineLifecycleCommands.ValidateGrant(grant);
            AcceptedEnrollmentIssuer? issuer = image.OnboardingContracts?.Issuers?.GetValueOrDefault(grant.IssuerEpoch);
            OperationalCredential? credential = image.Operations.Credentials.GetValueOrDefault(grant.CredentialId);
            if (issuer is null || credential is null || credential.Fingerprint != grant.LeafDigest
                || !historical && (issuer.RevokedAtUtc.HasValue || !processRenewal && issuer.RetiredAtUtc.HasValue
                    || credential.RevokedAtUtc.HasValue))
            {
                return false;
            }

            using X509Certificate2 root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(issuer.CertificateDer));
            using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(credential.PublicCertificate));
            if (!EnrollmentCertificates.ValidateIssuer(root, grant.ClusterId, grant.Generation, grant.IssuerEpoch, now)
                || EnrollmentCertificates.Hash(root.RawData) != issuer.CertificateDigest
                || EnrollmentCertificates.Hash(leaf.RawData) != grant.LeafDigest
                || EnrollmentCertificates.Hash(leaf.PublicKey.ExportSubjectPublicKeyInfo()) != grant.SpkiDigest)
            {
                return false;
            }

            EnrollmentRequest request = new(1, grant.ClusterId, grant.Generation, grant.AttemptId, grant.AttemptId,
                grant.NodeId, grant.StorageGeneration, grant.PeerUri, grant.SpkiDigest, new string('0', 64));
            EnrollmentIssuance template = new(1, grant.CredentialId, grant.IssuerEpoch, leaf.SerialNumber,
                grant.NodeId, grant.StorageGeneration, grant.PeerUri, grant.SpkiDigest, request.CsrDigest,
                new DateTimeOffset(leaf.NotBefore.ToUniversalTime()), new DateTimeOffset(leaf.NotAfter.ToUniversalTime()));
            if (!EnrollmentCertificates.ValidateLeaf(leaf, root, request, template, grant.LeafDigest, now, true))
            {
                return false;
            }

            byte[] signature = Convert.FromBase64String(grant.Signature);
            X509Certificate2 signer = root;
            if (processRenewal)
            {
                if (grant.Purpose != OnlineGrantPurpose.Process)
                {
                    return false;
                }

                signer = leaf;
            }

            using ECDsa? key = signer.GetECDsaPublicKey();
            return key is not null && signature.Length is >= 8 and <= 80
                && key.VerifyData(OnlineLifecycleCommands.Canonical(grant, processRenewal), signature,
                    HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception error) when (error is CryptographicException or InvalidDataException or FormatException or ArgumentException)
        {
            return false;
        }
    }
}
