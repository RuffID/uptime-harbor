using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

/// <summary>Выбирает политику для принятого credential до проверки цепочки.</summary>
public static class VoterCredentialLeafTrust
{
    public static bool RequiresDedicatedTrust(RevisionImage? image, OperationalCredential? credential, X509Certificate2 leaf)
    {
        if (leaf.GetNameInfo(X509NameType.SimpleName, forIssuer: true).StartsWith("UptimeHarbor Enrollment ", StringComparison.Ordinal)
            || credential is not null && (image?.OnboardingContracts?.VoterCredentialRotations?.ContainsKey(credential.Id) == true
                || credential.AdmissionId.HasValue || image?.OnboardingContracts?.Genesis?.NodeId == credential.NodeId))
        {
            return true;
        }

        foreach (AcceptedEnrollmentIssuer issuer in image?.OnboardingContracts?.Issuers?.Values ?? [])
        {
            using X509Certificate2 root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(issuer.CertificateDer));

            if (leaf.IssuerName.RawData.AsSpan().SequenceEqual(root.SubjectName.RawData))
            {
                return true;
            }
        }

        return false;
    }

    public static bool ValidateDedicated(RevisionImage image, OperationalCredential credential, X509Certificate2 leaf,
        DateTimeOffset now, bool server)
    {
        VoterCredentialRotation? rotation = image.OnboardingContracts?.VoterCredentialRotations?.GetValueOrDefault(credential.Id);
        if (rotation is null)
        {
            return EnrollmentLiveTrust.Validate(image, leaf, now);
        }

        EnrollmentIssuance template = rotation.Renewal.Issuance;
        AcceptedEnrollmentIssuer? issuer = image.OnboardingContracts?.Issuers?.GetValueOrDefault(template.IssuerEpoch);
        CommandResult? bind = null;
        if (rotation.BindOperationId is Guid operationId)
        {
            bind = image.Results.GetValueOrDefault(operationId);
        }

        OperationalNode? node = image.Operations.Nodes.GetValueOrDefault(credential.NodeId);
        if (!VoterCredentialPolicy.IsUsable(image, credential, now)
            || credential.AdmissionId.HasValue || node is not { Voting: true }
            || template.NodeId != node.NodeId || template.StorageGeneration != node.StorageGeneration || template.PeerUri != node.Address
            || template.IssueId != credential.Id || template.NotAfterUtc != credential.ExpiresAtUtc
            || credential.PublicCertificate != Convert.ToBase64String(leaf.RawData))
        {
            return false;
        }

        if (rotation.CredentialRevision <= 0 || rotation.CredentialRevision > image.Index
            || rotation.BoundAtUtc is not { } boundAt || now < boundAt || bind is null
            || bind.OperationId != rotation.BindOperationId || bind.Outcome != OperationOutcome.Committed
            || bind.Digest != rotation.BindDigest || bind.Index != rotation.CredentialRevision || bind.AppliedRevision != bind.Index
            || issuer is null || issuer.Epoch != template.IssuerEpoch || issuer.Purpose != EnrollmentIssuer.PEER_PURPOSE
            || issuer.RevokedAtUtc.HasValue || now < issuer.NotBeforeUtc || now >= issuer.NotAfterUtc)
        {
            return false;
        }

        using X509Certificate2 root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(issuer.CertificateDer));

        EnrollmentRequest request = new(1, image.ClusterId, image.Generation, credential.Id, credential.Id,
            node.NodeId, node.StorageGeneration, node.Address, template.SpkiDigest, template.CsrDigest);

        return EnrollmentCertificates.Hash(root.RawData) == issuer.CertificateDigest
            && EnrollmentCertificates.Hash(root.PublicKey.ExportSubjectPublicKeyInfo()) == issuer.SpkiDigest
            && EnrollmentCertificates.ValidateLeaf(leaf, root, request, template, credential.Fingerprint, now, server);
    }

    public static bool ValidatePrevious(RevisionImage image, OperationalCredential credential, X509Certificate2 leaf,
        DateTimeOffset now, VoterCredentialLegacyTrust? legacy)
    {
        OperationalNode? node = image.Operations.Nodes.GetValueOrDefault(credential.NodeId);
        if (node is not { Voting: true } || credential.AdmissionId.HasValue && !credential.ConsumedAtUtc.HasValue
            || !VoterCredentialPolicy.IsUsable(image, credential, now)
            || credential.PublicCertificate != Convert.ToBase64String(leaf.RawData)
            || !string.Equals(credential.Fingerprint, EnrollmentCertificates.Hash(leaf.RawData), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (RequiresDedicatedTrust(image, credential, leaf))
        {
            return ValidateDedicated(image, credential, leaf, now, server: false);
        }

        return legacy?.Validate(image, node, credential, leaf, now) == true;
    }
}
