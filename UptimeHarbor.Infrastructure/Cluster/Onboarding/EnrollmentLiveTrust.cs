using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Application revocation и точный leaf проверяются по текущему registry на каждом TLS/RPC вызове.</summary>
public static class EnrollmentLiveTrust
{
    public static bool Validate(RevisionImage image, X509Certificate2 leaf, DateTimeOffset now)
    {
        string fingerprint = EnrollmentCertificates.Hash(leaf.RawData);
        OperationalCredential? credential = image.Operations.Credentials.Values
            .SingleOrDefault(item => item.Fingerprint == fingerprint);
        OperationalNode? node = null;
        if (credential is not null)
        {
            node = image.Operations.Nodes.GetValueOrDefault(credential.NodeId);
        }

        if (credential is null || node is null || node.Removed || credential.RevokedAtUtc.HasValue
            || now >= credential.ExpiresAtUtc || Convert.ToBase64String(leaf.RawData) != credential.PublicCertificate)
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
            if (issuer.RevokedAtUtc.HasValue || now < issuer.NotBeforeUtc || now >= issuer.NotAfterUtc)
            {
                continue;
            }

            using X509Certificate2 root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(issuer.CertificateDer));
            if (!leaf.IssuerName.RawData.AsSpan().SequenceEqual(root.SubjectName.RawData)
                || EnrollmentCertificates.Hash(root.RawData) != issuer.CertificateDigest)
            {
                continue;
            }

            string spki = EnrollmentCertificates.Hash(leaf.PublicKey.ExportSubjectPublicKeyInfo());
            EnrollmentRequest request = new(1, image.ClusterId, image.Generation, credential.Id, credential.Id,
                node.NodeId, node.StorageGeneration, node.Address, spki, new string('0', 64));
            EnrollmentIssuance template = new(1, credential.Id, issuer.Epoch, leaf.SerialNumber,
                node.NodeId, node.StorageGeneration, node.Address, spki, request.CsrDigest,
                new DateTimeOffset(leaf.NotBefore.ToUniversalTime()), new DateTimeOffset(leaf.NotAfter.ToUniversalTime()));
            return EnrollmentCertificates.ValidateLeaf(leaf, root, request, template, credential.Fingerprint, now, false);
        }

        return false;
    }
}
