using System.Text;

namespace UptimeHarbor.Domain.Cluster;

public class ProtectedCredential
{
    public ProtectedCredential(string passwordHash, int formatVersion, string securityStamp, string concurrencyStamp)
    {
        if (formatVersion != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), "Unsupported password hash format.");
        }

        PasswordHash = StorageGuards.Text(passwordHash, 2048, nameof(passwordHash));
        SecurityStamp = StorageGuards.Text(securityStamp, 256, nameof(securityStamp));
        ConcurrencyStamp = StorageGuards.Text(concurrencyStamp, 256, nameof(concurrencyStamp));
        FormatVersion = formatVersion;
    }

    public string PasswordHash { get; }
    public int FormatVersion { get; }
    public string SecurityStamp { get; }
    public string ConcurrencyStamp { get; }
}
