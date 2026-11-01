using System.Text.Json;
using DotNext.Net.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster;

public class ClusterStorageLease : IDisposable
{
    private readonly FileStream file;
    public bool ColdStart { get; }
    public bool FirstStart { get; }

    public ClusterStorageLease(ClusterOptions options)
    {
        string root = options.StoragePath;
        // Provisioning создаёт manifest офлайн; отсутствие файлов не означает новую сетку.
        if (!Directory.Exists(root))
        {
            throw new InvalidDataException("Cluster storage must be provisioned before startup.");
        }

        file = new FileStream(Path.Combine(root, "instance.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
        try
        {
            ClusterDiskManifest? manifest = JsonSerializer.Deserialize<ClusterDiskManifest>(
                File.ReadAllBytes(Path.Combine(root, "manifest.json")));
            if (manifest is null || manifest.ClusterId != options.ClusterId || manifest.Generation != options.Generation
                || manifest.NodeId != options.NodeId || manifest.StorageGeneration != options.StorageGeneration
                || manifest.PublicUri != options.PublicUri)
            {
                throw new InvalidDataException("Cluster manifest does not match this instance.");
            }

            string started = Path.Combine(root, "started");
            string state = Path.Combine(root, "wal", "state");
            string membership = Path.Combine(root, "membership");
            if (File.Exists(started))
            {
                if (!File.Exists(state) || new FileInfo(state).Length != 1 + ClusterMemberId.Size + sizeof(long)
                    || !File.Exists(membership) || new FileInfo(membership).Length < sizeof(long))
                {
                    throw new InvalidDataException("Previously started cluster storage is incomplete.");
                }
            }
            else
            {
                FirstStart = true;
                if (options.Mode == "Restart" || File.Exists(state) || File.Exists(membership))
                {
                    throw new InvalidDataException("Cluster startup marker is missing.");
                }

                if (options.Mode == "Bootstrap" && manifest.BootstrapId == Guid.Empty)
                {
                    throw new InvalidDataException("Singleton bootstrap was not provisioned.");
                }

                ColdStart = options.Mode == "Bootstrap";
                using FileStream marker = new(started, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 1, FileOptions.WriteThrough);
                marker.WriteByte(1);
                marker.Flush(true);
            }
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        file.Dispose();
    }
}
