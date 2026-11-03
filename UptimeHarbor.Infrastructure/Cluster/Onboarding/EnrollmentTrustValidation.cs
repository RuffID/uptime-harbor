using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Криптографическая граница перед принятием public anchor; без загрузок и system trust mutations.</summary>
public static class EnrollmentTrustValidation
{
    public static void Validate(RevisionCommand command, DateTimeOffset now)
    {
        EnrollmentTrustCommands.Validate(command);
        EnrollmentTrustCommand data = command.EnrollmentTrust!;
        if (data.Issuer is null)
        {
            return;
        }

        try
        {
            AcceptedEnrollmentIssuer issuer = data.Issuer;
            using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(issuer.CertificateDer));
            if (!EnrollmentCertificates.ValidateIssuer(certificate, command.ClusterId, command.ClusterGeneration, issuer.Epoch, now)
                || EnrollmentCertificates.Hash(certificate.PublicKey.ExportSubjectPublicKeyInfo()) != issuer.SpkiDigest
                || certificate.NotBefore.ToUniversalTime() != issuer.NotBeforeUtc.UtcDateTime
                || certificate.NotAfter.ToUniversalTime() != issuer.NotAfterUtc.UtcDateTime)
            {
                throw new InvalidDataException("EnrollmentIssuerCertificateInvalid");
            }

            if (command.Action == "InitializeCluster")
            {
                ValidateGenesisLeaf(command, certificate, now);
            }
        }
        catch (Exception error) when (error is CryptographicException or FormatException or ArgumentException)
        {
            throw new InvalidDataException("EnrollmentTrustCertificateInvalid");
        }
    }

    private static void ValidateGenesisLeaf(RevisionCommand command, X509Certificate2 issuer, DateTimeOffset now)
    {
        EnrollmentTrustCommand data = command.EnrollmentTrust!;
        GenesisAuthorization genesis = data.Genesis!;
        OperationalCredential credential = data.Credential!;
        using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(credential.PublicCertificate));
        string spki = EnrollmentCertificates.Hash(leaf.PublicKey.ExportSubjectPublicKeyInfo());
        // CSR policy не выводится из leaf. Здесь проверяется уже выданный exact genesis artifact.
        EnrollmentRequest request = new(1, command.ClusterId, command.ClusterGeneration, genesis.GenesisId, genesis.GenesisId,
            genesis.NodeId, genesis.StorageGeneration, genesis.PeerUri, spki, new string('0', 64));
        EnrollmentIssuance template = new(1, credential.Id, data.IssuerEpoch, leaf.SerialNumber,
            genesis.NodeId, genesis.StorageGeneration, genesis.PeerUri, spki, request.CsrDigest,
            new DateTimeOffset(leaf.NotBefore.ToUniversalTime()), new DateTimeOffset(leaf.NotAfter.ToUniversalTime()));
        if (credential.ExpiresAtUtc != template.NotAfterUtc
            || !EnrollmentCertificates.ValidateLeaf(leaf, issuer, request, template, credential.Fingerprint, now, true))
        {
            throw new InvalidDataException("GenesisCredentialInvalid");
        }
    }
}
