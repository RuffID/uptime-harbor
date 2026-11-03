using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Flush snapshot/SQL checkpoint предшествует атомарному seal. Restore/rollback не получает online isolation.</summary>
public class ManagedStorageSeals(IOptions<InstallationOptions> options, IOptions<ClusterOptions> cluster,
    InstallationState installation, LocalSecretStore secrets)
{
    private readonly SemaphoreSlim serial = new(1, 1);

    public bool Exists => File.Exists(SealPath());

    public ManagedStorageSeal Read(LocalInstallationProfile profile)
    {
        string path = SealPath();
        ProtectedInstallationFiles.VerifyPermissions(path);
        if (new FileInfo(path).Length > 16384 || profile.AttemptId is null)
        {
            throw new InvalidDataException("ManagedStorageSealInvalid");
        }

        byte[] bytes = secrets.ReadRestart(profile.AttemptId.Value, File.ReadAllText(path, Encoding.UTF8));
        try
        {
            ManagedStorageSeal seal = JsonSerializer.Deserialize<ManagedStorageSeal>(bytes)
                ?? throw new InvalidDataException("ManagedStorageSealMissing");
            bool exact = seal.Version == 1 && seal.InstallIdentity == profile.InstallIdentity
                && seal.AttemptId == profile.AttemptId && seal.ClusterId == profile.ClusterId
                && seal.Generation == profile.ClusterGeneration && seal.NodeId == profile.NodeId
                && seal.StorageGeneration == profile.StorageGeneration && seal.PeerAddress == profile.PeerAddress
                && seal.KeyReference == profile.KeyReference && seal.CredentialId == profile.CredentialId
                && seal.TrustAnchorFingerprint == profile.TrustAnchorFingerprint
                && seal.ProfileRevision <= profile.ProfileRevision && seal.Index > 0 && seal.Term > 0
                && seal.SnapshotId != Guid.Empty && seal.ManifestDigest == ManifestDigest();
            if (!exact)
            {
                throw new InvalidDataException("ManagedStorageSealBindingInvalid");
            }

            RequireWholeStorage();
            return seal;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async Task WriteAsync(PublishedRevisionSnapshot snapshot, CancellationToken token)
    {
        if (!options.Value.Setup)
        {
            return;
        }

        await serial.WaitAsync(token);
        try
        {
            LocalInstallationProfile profile = installation.Current ?? throw new InvalidDataException("InstallationProfileMissing");
            RevisionImage image = snapshot.Image;
            if (profile.AttemptId is null || profile.CredentialId is null || profile.KeyReference is null
                || profile.TrustAnchorFingerprint is null || image.ClusterId != profile.ClusterId
                || image.Generation != profile.ClusterGeneration || image.Operations.MaintenanceId != Guid.Empty)
            {
                throw new InvalidDataException("ManagedCheckpointIdentityInvalid");
            }

            if (Exists)
            {
                ManagedStorageSeal previous = Read(profile);
                if (previous.Index > image.Index)
                {
                    throw new InvalidDataException("ManagedCheckpointRollbackRejected");
                }
            }

            ManagedStorageSeal seal = new(1, profile.InstallIdentity, profile.AttemptId.Value, image.ClusterId,
                image.Generation, profile.NodeId!.Value, profile.StorageGeneration!.Value, profile.PeerAddress!,
                profile.KeyReference, profile.CredentialId.Value, profile.TrustAnchorFingerprint, profile.ProfileRevision,
                ManifestDigest(), snapshot.Manifest.SnapshotId, snapshot.Digest, image.Index, image.Term,
                image.Digest!, image.Operations.MembershipVersion);
            string protectedState = secrets.ProtectRestart(profile.AttemptId.Value, JsonSerializer.SerializeToUtf8Bytes(seal));
            string path = SealPath();
            string next = path + ".next";
            if (File.Exists(next))
            {
                // Не выбирать более новый orphan/неизвестный эффект вместо durable seal.
                throw new InvalidDataException("ManagedCheckpointOutcomeUnknown");
            }

            ProtectedInstallationFiles.WriteNew(next, Encoding.UTF8.GetBytes(protectedState));
            File.Move(next, path, true);
            ProtectedInstallationFiles.SyncDirectory(options.Value.StateDirectory);
        }
        finally
        {
            serial.Release();
        }
    }

    public void RequireWholeStorage()
    {
        string root = cluster.Value.StoragePath;
        if (File.Exists(SealPath() + ".next") || !File.Exists(Path.Combine(root, "started"))
            || !File.Exists(Path.Combine(root, "wal", "state")) || !File.Exists(Path.Combine(root, "membership")))
        {
            throw new InvalidDataException("ManagedStorageIncompleteOfflineRecoveryRequired");
        }

        foreach (string directory in new[] { root, options.Value.StateDirectory })
        {
            ProtectedInstallationFiles.VerifyPermissions(directory, true);
            foreach (string marker in new[] { "blocked", "maintenance", "backup", "restore", "recovery" })
            {
                if (File.Exists(Path.Combine(directory, marker)) || Directory.Exists(Path.Combine(directory, marker)))
                {
                    throw new InvalidDataException("ManagedStorageOfflineMarker");
                }
            }
        }
    }

    private string ManifestDigest()
    {
        string path = Path.Combine(cluster.Value.StoragePath, "manifest.json");
        ProtectedInstallationFiles.VerifyPermissions(path);
        if (new FileInfo(path).Length > 8192)
        {
            throw new InvalidDataException("ManagedManifestLimit");
        }

        return RevisionCodec.Digest(File.ReadAllBytes(path));
    }

    private string SealPath()
    {
        return Path.Combine(ProtectedInstallationFiles.DirectoryPath(options.Value.StateDirectory), "managed-storage.seal");
    }
}
