using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Аутентифицированные immutable файлы. SQL reference публикуется отдельно после flush.</summary>
public class LocalSecretStore(IOptions<InstallationOptions> configured, InstallationLease lease)
{
    public void Initialize()
    {
        byte[] key = WrappingKey(true);
        CryptographicOperations.ZeroMemory(key);
    }

    private byte[] WrappingKey(bool create)
    {
        string path = Path.Combine(configured.Value.SecretDirectory, "wrapping.key");
        if (!File.Exists(path))
        {
            if (!create || Directory.EnumerateFiles(configured.Value.KeyDirectory, "*.key").Any())
            {
                throw new InvalidDataException("WrappingKeyMissing");
            }

            byte[] generated = RandomNumberGenerator.GetBytes(32);
            try
            {
                ProtectedInstallationFiles.WriteNew(path, generated);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(generated);
            }
        }

        ProtectedInstallationFiles.VerifyPermissions(path);
        byte[] key = File.ReadAllBytes(path);
        if (key.Length != 32)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidDataException("WrappingKeyInvalid");
        }

        return key;
    }

    public string Write(Guid attemptId, string purpose, ReadOnlySpan<byte> plaintext)
    {
        if (attemptId == Guid.Empty || purpose is not ("peer" or "bootstrap" or "issuer"))
        {
            throw new ArgumentException("SecretPurposeInvalid");
        }

        Guid id = Guid.NewGuid();
        byte[] key = WrappingKey(true);
        byte[] envelope = new byte[1 + 12 + 16 + plaintext.Length];
        envelope[0] = 1;
        RandomNumberGenerator.Fill(envelope.AsSpan(1, 12));
        try
        {
            using AesGcm cipher = new(key, 16);
            cipher.Encrypt(envelope.AsSpan(1, 12), plaintext, envelope.AsSpan(29), envelope.AsSpan(13, 16),
                AssociatedData(attemptId, purpose, id));
            ProtectedInstallationFiles.WriteNew(Path.Combine(configured.Value.KeyDirectory, id.ToString("N") + ".key"), envelope);
            return id.ToString("N") + ":" + Convert.ToHexString(SHA256.HashData(envelope));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    public byte[] Read(Guid attemptId, string purpose, string reference)
    {
        string[] parts = reference.Split(':');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out Guid id)
            || parts[1].Length != 64 || !parts[1].All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("SecretReferenceInvalid");
        }

        string path = Path.Combine(configured.Value.KeyDirectory, id.ToString("N") + ".key");
        ProtectedInstallationFiles.VerifyPermissions(path);
        FileInfo info = new(path);
        if (info.Length is < 30 or > 65536)
        {
            throw new InvalidDataException("SecretFileInvalid");
        }

        byte[] envelope = File.ReadAllBytes(path);
        if (envelope[0] != 1 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(envelope), Convert.FromHexString(parts[1])))
        {
            throw new InvalidDataException("SecretDigestInvalid");
        }

        byte[] key = WrappingKey(false);
        byte[] plaintext = new byte[envelope.Length - 29];
        try
        {
            using AesGcm cipher = new(key, 16);
            cipher.Decrypt(envelope.AsSpan(1, 12), envelope.AsSpan(29), envelope.AsSpan(13, 16), plaintext,
                AssociatedData(attemptId, purpose, id));
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidDataException("SecretAuthenticationFailed");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    // Invitation живёт в SQL, поэтому её envelope не создаёт orphan-файл после accepted reserve.
    public string ProtectInvitation(Guid attemptId, ReadOnlySpan<byte> invitation)
    {
        return ProtectState(attemptId, "invitation", invitation, 4096);
    }

    public string ProtectRestart(Guid attemptId, ReadOnlySpan<byte> state)
    {
        return ProtectState(attemptId, "managed-restart", state, 8192);
    }

    private string ProtectState(Guid attemptId, string purpose, ReadOnlySpan<byte> invitation, int maximum)
    {
        if (attemptId == Guid.Empty || invitation.Length < 1 || invitation.Length > maximum)
        {
            throw new ArgumentException("InvitationInvalid");
        }

        byte[] key = WrappingKey(true);
        byte[] envelope = new byte[29 + invitation.Length];
        envelope[0] = 1;
        RandomNumberGenerator.Fill(envelope.AsSpan(1, 12));
        try
        {
            using AesGcm cipher = new(key, 16);
            cipher.Encrypt(envelope.AsSpan(1, 12), invitation, envelope.AsSpan(29), envelope.AsSpan(13, 16),
                AssociatedData(attemptId, purpose, attemptId));
            return Convert.ToBase64String(envelope);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    public byte[] ReadInvitation(Guid attemptId, string protectedInvitation)
    {
        return ReadState(attemptId, "invitation", protectedInvitation, 4096);
    }

    public byte[] ReadRestart(Guid attemptId, string protectedState)
    {
        return ReadState(attemptId, "managed-restart", protectedState, 8192);
    }

    private byte[] ReadState(Guid attemptId, string purpose, string protectedInvitation, int maximum)
    {
        byte[] envelope = Convert.FromBase64String(protectedInvitation);
        if (envelope.Length < 30 || envelope.Length > maximum + 29 || envelope[0] != 1)
        {
            throw new InvalidDataException("InvitationEnvelopeInvalid");
        }

        byte[] key = WrappingKey(false);
        byte[] plaintext = new byte[envelope.Length - 29];
        try
        {
            using AesGcm cipher = new(key, 16);
            cipher.Decrypt(envelope.AsSpan(1, 12), envelope.AsSpan(29), envelope.AsSpan(13, 16), plaintext,
                AssociatedData(attemptId, purpose, attemptId));
            return plaintext;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidDataException("InvitationAuthenticationFailed");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    public void Reconcile(Guid attemptId, string? reference)
    {
        if (reference is null)
        {
            return;
        }

        byte[] material = Read(attemptId, "peer", reference);
        CryptographicOperations.ZeroMemory(material);
        // Неизвестные файлы не выбираются по имени/времени и не публикуются как credential.
    }

    public void ReconcileAll(LocalInstallationProfile? profile, IReadOnlyList<EnrollmentIssuerReference>? issuers = null)
    {
        HashSet<string> expected = new(StringComparer.OrdinalIgnoreCase);
        if (profile?.KeyReference is not null)
        {
            Reconcile(profile.AttemptId!.Value, profile.KeyReference);
            expected.Add(profile.KeyReference.Split(':')[0] + ".key");
        }

        if (profile?.BootstrapAccountReference is not null)
        {
            byte[] account = Read(profile.AttemptId!.Value, "bootstrap", profile.BootstrapAccountReference);
            try
            {
                if (UptimeHarbor.Application.Cluster.RevisionCodec.Digest(account) != profile.BootstrapAccountDigest)
                {
                    throw new InvalidDataException("BootstrapAccountDigestMismatch");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(account);
            }

            expected.Add(profile.BootstrapAccountReference.Split(':')[0] + ".key");
        }

        foreach (EnrollmentIssuerReference issuer in issuers ?? [])
        {
            bool local = profile is not null && profile.InstallIdentity == issuer.InstallIdentity
                && profile.ClusterId == issuer.ClusterId && profile.ClusterGeneration == issuer.Generation
                && profile.NodeId == issuer.NodeId && profile.StorageGeneration == issuer.StorageGeneration;
            if (!local)
            {
                throw new InvalidDataException("ForeignEnrollmentIssuerReference");
            }

            byte[] key = Read(issuer.Epoch, "issuer", issuer.KeyReference);
            CryptographicOperations.ZeroMemory(key);
            if (!expected.Add(issuer.KeyReference.Split(':')[0] + ".key"))
            {
                throw new InvalidDataException("DuplicateEnrollmentIssuerReference");
            }
        }

        foreach (string path in Directory.EnumerateFileSystemEntries(configured.Value.KeyDirectory))
        {
            string name = Path.GetFileName(path);
            if (name != "installation.lock" && !expected.Contains(name))
            {
                throw new InvalidDataException("OrphanSecretNeedsAction");
            }
        }
    }

    private byte[] AssociatedData(Guid attemptId, string purpose, Guid id)
    {
        return Encoding.UTF8.GetBytes($"UptimeHarbor.LocalSecret.v1/{lease.InstallIdentity:N}/{attemptId:N}/{purpose}/{id:N}");
    }
}
