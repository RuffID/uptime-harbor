using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Локальный immutable public trust manifest. SQL intent предшествует файлу; файл не предоставляет authority.</summary>
public class EnrollmentTrustInstallation(IOptions<InstallationOptions> options, InstallationLease lease,
    LocalSecretStore secrets, IOnboardingProgressUnitOfWork work, TimeProvider time)
{
    public async Task<LocalOnboardingAttempt> InstallAsync(LocalOnboardingAttempt local, CancellationToken token)
    {
        EnrollmentDescriptor descriptor = local.Descriptor ?? throw new InvalidDataException("EnrollmentDescriptorMissing");
        EnrollmentDescriptorValidation.Bound(descriptor, local, descriptor.ResponseProof
            ?? throw new InvalidDataException("EnrollmentResponseProofMissing"),
            new(new SetupPeerAddressPolicy(options.Value)), time.GetUtcNow());
        if (descriptor.Status != "Admitted" || descriptor.AdmissionUntilUtc <= time.GetUtcNow()
            || descriptor.BootId != lease.BootId || descriptor.BootChallenge != lease.BootChallenge)
        {
            throw new InvalidDataException("EnrollmentTrustAdmissionRequired");
        }

        if (local.TrustReference is not null)
        {
            // Повтор после crash может завершить только уже записанный SQL intent.
            byte[] expected = Encode(local);
            RequireReference(local.TrustReference, expected);
            WriteOrVerify(local.TrustReference, expected);
            using X509Certificate2 certificate = Read(local);
            return local;
        }

        byte[] bytes = Encode(local);
        string reference = EnrollmentCertificates.Hash(bytes);
        LocalOnboardingAttempt next = local with
        {
            TrustReference = reference, TrustManifest = Encoding.UTF8.GetString(bytes), Revision = local.Revision + 1
        };
        await work.Attempts.ReplaceAsync(next, local.Revision, token);
        await work.SaveChangesAsync(token);
        // Отмена после SQL commit не оставляет успешную установку без файла.
        WriteOrVerify(reference, bytes);
        using X509Certificate2 installed = Read(next);
        return next;
    }

    public X509Certificate2 Read(LocalOnboardingAttempt local)
    {
        string reference = local.TrustReference ?? throw new InvalidDataException("EnrollmentTrustReferenceMissing");
        byte[] bytes = Encode(local);
        RequireReference(reference, bytes);
        string path = FilePath(reference);
        ProtectedInstallationFiles.VerifyPermissions(DirectoryPath(), true);
        ProtectedInstallationFiles.VerifyPermissions(path);
        FileInfo file = new(path);
        if (file.Length != bytes.Length || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            throw new InvalidDataException("EnrollmentTrustFileChanged");
        }

        EnrollmentDescriptor descriptor = local.Descriptor!;
        EnrollmentIssuance template = descriptor.Issuance!;
        AcceptedEnrollmentIssuer issuer = descriptor.Issuers.Single(item => item.Epoch == template.IssuerEpoch);
        using X509Certificate2 root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(issuer.CertificateDer));
        using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(descriptor.CertificateDer!));
        if (!EnrollmentCertificates.ValidateLeaf(leaf, root, local.Request, template,
            EnrollmentCertificates.Hash(leaf.RawData), time.GetUtcNow(), true))
        {
            throw new InvalidDataException("EnrollmentTrustCertificateInvalid");
        }

        byte[] pem = secrets.Read(local.AttemptId, "peer", local.PrivateKeyReference);
        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportFromPem(Encoding.UTF8.GetString(pem));
            if (EnrollmentCertificates.Hash(key.ExportSubjectPublicKeyInfo()) != local.Request.SpkiDigest)
            {
                throw new InvalidDataException("EnrollmentTrustKeyChanged");
            }

            return leaf.CopyWithPrivateKey(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pem);
        }
    }

    public void Reconcile(LocalInstallationProfile? profile, LocalOnboardingAttempt? local)
    {
        string directory = DirectoryPath();
        if (local?.TrustReference is not null)
        {
            if (profile is null || profile.AttemptId != local.AttemptId || profile.KeyReference != local.PrivateKeyReference
                || profile.NodeId != local.Request.NodeId || profile.StorageGeneration != local.Request.StorageGeneration)
            {
                throw new InvalidDataException("EnrollmentTrustProfileChanged");
            }

            using X509Certificate2 certificate = Read(local);
        }

        if (!Directory.Exists(directory))
        {
            return;
        }

        ProtectedInstallationFiles.VerifyPermissions(directory, true);
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            if (local?.TrustReference is null || Path.GetFileName(path) != local.TrustReference + ".trust")
            {
                throw new InvalidDataException("OrphanEnrollmentTrustNeedsAction");
            }
        }
    }

    private byte[] Encode(LocalOnboardingAttempt local)
    {
        EnrollmentDescriptor descriptor = local.Descriptor ?? throw new InvalidDataException("EnrollmentDescriptorMissing");
        EnrollmentIssuance template = descriptor.Issuance ?? throw new InvalidDataException("EnrollmentTemplateMissing");
        AcceptedEnrollmentIssuer issuer = descriptor.Issuers.Single(item => item.Epoch == template.IssuerEpoch);
        OperationalCredential credential = descriptor.Credentials.Single(item => item.Id == template.IssueId);
        OperationalNode node = descriptor.Nodes.Single(item => item.NodeId == local.Request.NodeId);
        if (local.RequestDigest != EnrollmentRequestCodec.Digest(local.Request) || node.Removed
            || node.StorageGeneration != local.Request.StorageGeneration || node.Address != local.Request.PeerUri
            || credential.NodeId != node.NodeId || credential.PublicCertificate != descriptor.CertificateDer
            || credential.RevokedAtUtc.HasValue || issuer.RevokedAtUtc.HasValue
            || descriptor.CertificateDer is null || local.ProtectedInvitation is not null)
        {
            throw new InvalidDataException("EnrollmentTrustRegistryInvalid");
        }

        // Process lease, admission deadline и mutable registry не меняют immutable trust bytes при resume.
        JsonElement binding = JsonSerializer.SerializeToElement(new
        {
            Version = 1, lease.InstallIdentity, local.AttemptId, local.Request, local.RequestDigest,
            local.PrivateKeyReference, local.BrowserOrigin, local.ReservationIndex,
            Issuance = template, descriptor.CertificateDer,
            issuer.Epoch, IssuerCertificateDer = issuer.CertificateDer, issuer.CertificateDigest, issuer.SpkiDigest,
            CredentialId = credential.Id, credential.Fingerprint
        });
        if (local.TrustManifest is not null)
        {
            byte[] saved = Encoding.UTF8.GetBytes(local.TrustManifest);
            using JsonDocument document = JsonDocument.Parse(saved);
            if (saved.Length > EnrollmentTransport.MAX_BYTES
                || !JsonElement.DeepEquals(document.RootElement.GetProperty("Binding"), binding))
            {
                throw new InvalidDataException("EnrollmentTrustIntentChanged");
            }

            return saved;
        }

        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            Binding = binding, descriptor.Nodes, descriptor.Credentials, descriptor.Issuers
        });
    }

    private void WriteOrVerify(string reference, byte[] bytes)
    {
        string directory = DirectoryPath();
        ProtectedInstallationFiles.EnsureDirectory(directory);
        string path = FilePath(reference);
        if (File.Exists(path))
        {
            ProtectedInstallationFiles.VerifyPermissions(path);
            if (new FileInfo(path).Length != bytes.Length || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            {
                throw new InvalidDataException("EnrollmentTrustFileChanged");
            }

            return;
        }

        ProtectedInstallationFiles.WriteNew(path, bytes);
    }

    private static void RequireReference(string reference, byte[] bytes)
    {
        if (reference.Length != 64 || !reference.All(Uri.IsHexDigit) || reference != EnrollmentCertificates.Hash(bytes))
        {
            throw new InvalidDataException("EnrollmentTrustReferenceInvalid");
        }
    }

    private string DirectoryPath()
    {
        return ProtectedInstallationFiles.DirectoryPath(Path.Combine(options.Value.StateDirectory, "enrollment-trust"));
    }

    private string FilePath(string reference)
    {
        if (reference.Length != 64 || !reference.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("EnrollmentTrustReferenceInvalid");
        }

        return Path.Combine(DirectoryPath(), reference + ".trust");
    }
}
