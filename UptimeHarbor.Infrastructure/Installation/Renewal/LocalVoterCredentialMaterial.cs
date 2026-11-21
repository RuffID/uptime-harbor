using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;

namespace UptimeHarbor.Infrastructure.Installation.Renewal;

/// <summary>Локальная криптографическая целостность; не предоставляет accepted/majority authority.</summary>
public static class LocalVoterCredentialMaterial
{
    public static string Reference(Guid install, Guid cluster, long generation, Guid node, Guid storage, Guid previous)
    {
        return Reference(install, cluster, generation, node, storage, previous, false);
    }

    internal static string Reference(Guid install, Guid cluster, long generation, Guid node, Guid storage, Guid previous,
        bool continuation)
    {
        return EnrollmentCertificates.Hash(StrictOnboardingJson.Encode(new
        {
            Domain = continuation ? "UptimeHarbor.LocalVoterCredential.ContinuationSlot.v1" : "UptimeHarbor.LocalVoterCredential.Slot.v1",
            InstallIdentity = install,
            ClusterId = cluster, Generation = generation, NodeId = node, StorageGeneration = storage,
            PreviousCredentialId = previous
        }));
    }

    public static byte[] Encode(LocalVoterCredentialIntent intent, ECDsa key)
    {
        byte[] metadata = StrictOnboardingJson.Encode(intent);
        byte[] privateKey = key.ExportPkcs8PrivateKey();
        try
        {
            byte[] bytes = new byte[4 + metadata.Length + privateKey.Length];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, metadata.Length);
            metadata.CopyTo(bytes, 4);
            privateKey.CopyTo(bytes, 4 + metadata.Length);
            return bytes;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    public static LocalVoterCredentialIntent Decode(ReadOnlySpan<byte> bytes, Guid install, string reference, out ECDsa key)
    {
        return Decode(bytes, install, reference, false, out key);
    }

    internal static LocalVoterCredentialIntent Decode(ReadOnlySpan<byte> bytes, Guid install, string reference,
        bool continuation, out ECDsa key)
    {
        if (bytes.Length < 5)
        {
            throw new InvalidDataException("VoterCredentialLocalIntentTruncated");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (length < 1 || length >= bytes.Length - 4)
        {
            throw new InvalidDataException("VoterCredentialLocalIntentTruncated");
        }

        LocalVoterCredentialIntent intent = StrictOnboardingJson.Decode<LocalVoterCredentialIntent>(bytes.Slice(4, length));
        VoterCredentialRequest request = intent.Request;
        EnrollmentIssuance template = request.Renewal.Issuance;
        VoterCredentialProofs.Digest(request);
        if (intent.Version != 1 || intent.InstallIdentity != install || intent.KeyReference != reference
            || continuation != (intent.Process is not null)
            || intent.PreviousKeyOwner == Guid.Empty || string.IsNullOrEmpty(intent.PreviousKeyReference)
            || Reference(install, request.ClusterId, request.Generation, template.NodeId, template.StorageGeneration,
                request.Renewal.PreviousCredentialId, continuation) != reference)
        {
            throw new InvalidDataException("VoterCredentialLocalIntentChanged");
        }

        key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(bytes[(4 + length)..], out int read);
            if (read != bytes.Length - 4 - length || key.KeySize != 256
                || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value
                || EnrollmentCertificates.Hash(key.ExportSubjectPublicKeyInfo()) != template.SpkiDigest)
            {
                throw new InvalidDataException("VoterCredentialLocalKeyChanged");
            }

            byte[] csr = Convert.FromBase64String(intent.CsrDer);
            if (Convert.ToBase64String(csr) != intent.CsrDer)
            {
                throw new InvalidDataException("VoterCredentialLocalCsrChanged");
            }

            EnrollmentCertificates.ReadCsr(csr, VoterCredentialProofs.CertificateIdentity(request));
            return intent;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    internal static ECDsa OpenPrevious(LocalVoterCredentialIntent intent, LocalSecretStore secrets,
        LocalVoterCredentialFiles files)
    {
        if (intent.Request.Renewal.PreviousCredentialRevision == 0)
        {
            return OpenBaseline(intent, secrets);
        }

        VoterCredentialRequest request = intent.Request;
        VoterCredentialRenewal renewal = request.Renewal;
        EnrollmentIssuance target = renewal.Issuance;
        (LocalVoterCredentialIntent previous, ECDsa key) = LocalVoterCredentialRetry.ReadIntent(
            files, intent.InstallIdentity, intent.PreviousKeyReference)
            ?? throw new InvalidDataException("VoterCredentialLocalPreviousIntentMissing");
        EnrollmentIssuance source = previous.Request.Renewal.Issuance;
        if (intent.PreviousKeyOwner == renewal.PreviousCredentialId && source.IssueId == renewal.PreviousCredentialId
            && source.SpkiDigest == renewal.PreviousSpkiDigest && previous.Request.ClusterId == request.ClusterId
            && previous.Request.Generation == request.Generation && source.NodeId == target.NodeId
            && source.StorageGeneration == target.StorageGeneration && source.PeerUri == target.PeerUri)
        {
            return key;
        }

        key.Dispose();
        throw new InvalidDataException("VoterCredentialLocalPreviousIntentChanged");
    }

    private static ECDsa OpenBaseline(LocalVoterCredentialIntent intent, LocalSecretStore secrets)
    {
        byte[] pem = secrets.Read(intent.PreviousKeyOwner, "peer", intent.PreviousKeyReference);
        ECDsa key = ECDsa.Create();
        try
        {
            key.ImportFromPem(Encoding.UTF8.GetString(pem));
            if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value
                || EnrollmentCertificates.Hash(key.ExportSubjectPublicKeyInfo()) != intent.Request.Renewal.PreviousSpkiDigest)
            {
                throw new InvalidDataException("VoterCredentialLocalPreviousKeyChanged");
            }

            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pem);
        }
    }

    public static void RequireAuthorization(LocalVoterCredentialIntent intent, LocalVoterCredentialAuthorization authorization)
    {
        VoterCredentialRotation rotation = authorization.Rotation;
        VoterCredentialRequest request = intent.Request;
        AcceptedEnrollmentIssuer issuer = authorization.Issuer;
        EnrollmentIssuance template = request.Renewal.Issuance;
        VoterCredentialRotation expected = new(request.Renewal, request.OperationId, rotation.AuthorizedIndex,
            RevisionCodec.Digest(VoterCredentialAuthorizationCommand.Create(request)), rotation.AuthorizedAtUtc);
        if (authorization.RequestDigest != VoterCredentialProofs.Digest(request) || rotation != expected
            || rotation.AuthorizedIndex < 1 || issuer.Epoch != template.IssuerEpoch
            || issuer.Purpose != EnrollmentIssuer.PEER_PURPOSE || issuer.RevokedAtUtc.HasValue || issuer.RetiredAtUtc.HasValue
            || rotation.AuthorizedAtUtc < template.NotAfterUtc.AddDays(-30)
            || rotation.AuthorizedAtUtc - template.NotAfterUtc.AddDays(-30) > TimeSpan.FromSeconds(10))
        {
            throw new InvalidDataException("VoterCredentialLocalAuthorizationChanged");
        }

        using X509Certificate2 root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(issuer.CertificateDer));
        if (EnrollmentCertificates.Hash(root.RawData) != issuer.CertificateDigest
            || EnrollmentCertificates.Hash(root.PublicKey.ExportSubjectPublicKeyInfo()) != issuer.SpkiDigest
            || issuer.Revision < 1 || issuer.NotBeforeUtc != new DateTimeOffset(root.NotBefore.ToUniversalTime())
            || issuer.NotAfterUtc != new DateTimeOffset(root.NotAfter.ToUniversalTime())
            || template.NotBeforeUtc < issuer.NotBeforeUtc || template.NotAfterUtc > issuer.NotAfterUtc
            || !EnrollmentCertificates.ValidateIssuer(root, request.ClusterId, request.Generation, issuer.Epoch,
                rotation.AuthorizedAtUtc))
        {
            throw new InvalidDataException("VoterCredentialLocalIssuerChanged");
        }
    }

    public static void RequireArtifact(LocalVoterCredentialIntent intent, LocalVoterCredentialAuthorization authorization,
        EnrollmentIssuedArtifact artifact)
    {
        VoterCredentialRequest request = intent.Request;
        EnrollmentIssuance template = request.Renewal.Issuance;
        if (artifact.IssueId != template.IssueId || artifact.IssuerEpoch != template.IssuerEpoch
            || artifact.AttemptId != request.OperationId || artifact.GenesisId.HasValue
            || artifact.AuthorizationIndex != authorization.Rotation.AuthorizedIndex
            || artifact.RequestDigest != authorization.RequestDigest
            || artifact.TemplateDigest != EnrollmentSigningJournal.TemplateDigest(template)
            || artifact.CertificateDer.Length is < 1 or > EnrollmentCertificates.MAX_DER_BYTES
            || EnrollmentCertificates.Hash(artifact.CertificateDer) != artifact.CertificateDigest)
        {
            throw new InvalidDataException("VoterCredentialLocalArtifactChanged");
        }

        using X509Certificate2 root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(authorization.Issuer.CertificateDer));
        using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(artifact.CertificateDer);
        if (!EnrollmentCertificates.ValidateLeaf(leaf, root, VoterCredentialProofs.CertificateIdentity(request), template,
            artifact.CertificateDigest, authorization.Rotation.AuthorizedAtUtc, false))
        {
            throw new InvalidDataException("VoterCredentialLocalLeafChanged");
        }
    }
}
