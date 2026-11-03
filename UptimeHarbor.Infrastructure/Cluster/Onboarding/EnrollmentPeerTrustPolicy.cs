using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Dedicated CA проверяется вместе с accepted credential; retirement сохраняет работающие leaf, revocation закрывает их.</summary>
public static class EnrollmentPeerTrustPolicy
{
    public static bool IsActive(RevisionImage image, OperationalNode node, OperationalCredential credential, DateTimeOffset now)
    {
        if (image.OnboardingContracts?.Issuers is null)
        {
            return true;
        }

        try
        {
            using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(credential.PublicCertificate));
            foreach (AcceptedEnrollmentIssuer issuer in image.OnboardingContracts.Issuers.Values)
            {
                using X509Certificate2 root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(issuer.CertificateDer));
                if (!leaf.IssuerName.RawData.AsSpan().SequenceEqual(root.SubjectName.RawData))
                {
                    continue;
                }

                if (issuer.RevokedAtUtc.HasValue || issuer.CertificateDigest != EnrollmentCertificates.Hash(root.RawData))
                {
                    return false;
                }

                string spki = EnrollmentCertificates.Hash(leaf.PublicKey.ExportSubjectPublicKeyInfo());
                EnrollmentRequest request = new(1, image.ClusterId, image.Generation, credential.Id, credential.Id,
                    node.NodeId, node.StorageGeneration, node.Address, spki, new string('0', 64));
                EnrollmentIssuance template = new(1, credential.Id, issuer.Epoch, leaf.SerialNumber, node.NodeId,
                    node.StorageGeneration, node.Address, spki, request.CsrDigest,
                    new DateTimeOffset(leaf.NotBefore.ToUniversalTime()), new DateTimeOffset(leaf.NotAfter.ToUniversalTime()));
                return EnrollmentCertificates.ValidateLeaf(leaf, root, request, template, credential.Fingerprint, now, true);
            }

            // Existing manual CA продолжает свой legacy TLS/CRL путь. Это не заменяет отдельную chain validation.
            return true;
        }
        catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or FormatException or InvalidDataException)
        {
            return false;
        }
    }
}
