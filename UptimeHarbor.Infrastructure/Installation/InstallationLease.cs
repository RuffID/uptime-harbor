using System.Text;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Один процесс на state и storage; удерживается до остановки host.</summary>
public class InstallationLease : IDisposable
{
    private readonly List<FileStream> locks = [];
    public Guid InstallIdentity { get; }
    public Guid BootId { get; } = Guid.NewGuid();

    public InstallationLease(InstallationOptions options, string storagePath)
    {
        string state = ProtectedInstallationFiles.DirectoryPath(options.StateDirectory);
        string secret = ProtectedInstallationFiles.DirectoryPath(options.SecretDirectory);
        string keys = ProtectedInstallationFiles.DirectoryPath(options.KeyDirectory);
        string storage = ProtectedInstallationFiles.DirectoryPath(storagePath);
        string[] paths = [state, secret, keys, storage];
        foreach (string path in paths)
        {
            if (paths.Any(other => other != path && (path.StartsWith(other + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) || path.Equals(other, StringComparison.OrdinalIgnoreCase))))
            {
                throw new InvalidDataException("InstallationVolumesMustBeSeparate");
            }
        }

        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
        {
            throw new InvalidDataException("InstallationVolumesMustBeSeparate");
        }

        try
        {
            foreach (string path in paths)
            {
                ProtectedInstallationFiles.EnsureDirectory(path);
                locks.Add(new(Path.Combine(path, "installation.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough));
            }

            string identity = Path.Combine(state, "install.id");
            if (!File.Exists(identity))
            {
                ProtectedInstallationFiles.WriteNew(identity, Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N")));
            }

            ProtectedInstallationFiles.VerifyPermissions(identity);
            if (!Guid.TryParseExact(File.ReadAllText(identity), "N", out Guid parsed) || parsed == Guid.Empty)
            {
                throw new InvalidDataException("InstallationIdentityInvalid");
            }

            InstallIdentity = parsed;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (FileStream file in locks)
        {
            file.Dispose();
        }

        locks.Clear();
    }
}
