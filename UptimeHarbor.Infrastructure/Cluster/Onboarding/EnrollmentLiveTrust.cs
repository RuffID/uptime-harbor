using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Application revocation и точный leaf проверяются по текущему registry на каждом TLS/RPC вызове.</summary>
public static class EnrollmentLiveTrust
{
    public static bool Validate(RevisionImage image, X509Certificate2 leaf, DateTimeOffset now)
    {
        string fingerprint = EnrollmentCertificates.Hash(leaf.RawData);
        OperationalCredential[] matches = image.Operations.Credentials.Values
            .Where(item => string.Equals(item.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (matches.Length != 1)
        {
            return false;
        }

        OperationalCredential credential = matches[0];
        OperationalNode? node = image.Operations.Nodes.GetValueOrDefault(credential.NodeId);
        if (node is null || !VoterCredentialPolicy.IsUsable(image, credential, now)
            || Convert.ToBase64String(leaf.RawData) != credential.PublicCertificate)
        {
            return false;
        }

        bool admitted = credential.AdmissionId.HasValue && !credential.ConsumedAtUtc.HasValue
            && credential.AdmissionExpiresAtUtc > now;
        if (!node.Voting && !admitted)
        {
            return false;
        }

        foreach (AcceptedEnrollmentIssuer issuer in image.OnboardingContracts?.Issuers?.Values ?? [])
        {
            if (issuer.Purpose != EnrollmentIssuer.PEER_PURPOSE || issuer.RevokedAtUtc.HasValue
                || now < issuer.NotBeforeUtc || now >= issuer.NotAfterUtc)
            {
                continue;
            }

            try
            {
                using X509Certificate2 root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(issuer.CertificateDer));
                if (!leaf.IssuerName.RawData.AsSpan().SequenceEqual(root.SubjectName.RawData))
                {
                    continue;
                }

                string spki = EnrollmentCertificates.Hash(leaf.PublicKey.ExportSubjectPublicKeyInfo());
                EnrollmentRequest request = new(1, image.ClusterId, image.Generation, credential.Id, credential.Id,
                    node.NodeId, node.StorageGeneration, node.Address, spki, new string('0', 64));
                EnrollmentIssuance template = new(1, credential.Id, issuer.Epoch, leaf.SerialNumber,
                    node.NodeId, node.StorageGeneration, node.Address, spki, request.CsrDigest,
                    new DateTimeOffset(leaf.NotBefore.ToUniversalTime()), new DateTimeOffset(leaf.NotAfter.ToUniversalTime()));
                return EnrollmentCertificates.Hash(root.RawData) == issuer.CertificateDigest
                    && EnrollmentCertificates.Hash(root.PublicKey.ExportSubjectPublicKeyInfo()) == issuer.SpkiDigest
                    && EnrollmentCertificates.ValidateLeaf(leaf, root, request, template, credential.Fingerprint, now, false);
            }
            catch (Exception error) when (error is CryptographicException or FormatException)
            {
                return false;
            }
        }

        return false;
    }
}
