using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Dedicated CA проверяется вместе с accepted credential; retirement сохраняет работающие leaf, revocation закрывает их.</summary>
public static class EnrollmentPeerTrustPolicy
{
    public static bool IsActive(RevisionImage image, OperationalNode node, OperationalCredential credential, DateTimeOffset now)
    {
        if (node.NodeId != credential.NodeId || image.Operations.Nodes.GetValueOrDefault(node.NodeId) != node
            || !VoterCredentialPolicy.IsUsable(image, credential, now))
        {
            return false;
        }

        if (image.OnboardingContracts?.Issuers is null)
        {
            return true;
        }

        try
        {
            using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(credential.PublicCertificate));
            if (image.OnboardingContracts.VoterCredentialRotations?.ContainsKey(credential.Id) == true)
            {
                return VoterCredentialLeafTrust.ValidateDedicated(image, credential, leaf, now, server: true);
            }

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
