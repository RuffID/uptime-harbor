using Microsoft.Extensions.Options;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Issuers;

/// <summary>Повторно использует managed secrets либо открывает отдельные настроенные volumes ручной установки.</summary>
public class EnrollmentIssuerStorage(IOptions<InstallationOptions> installation, IOptions<ClusterOptions> cluster,
    InstallationLease? managedLease = null, LocalSecretStore? managedSecrets = null) : IDisposable
{
    private readonly object sync = new();
    private InstallationLease? ownedLease;
    private LocalSecretStore? ownedSecrets;

    public (InstallationLease Lease, LocalSecretStore Secrets) Open(bool create)
    {
        lock (sync)
        {
            if (managedLease is not null && managedSecrets is not null)
            {
                return (managedLease, managedSecrets);
            }

            InstallationOptions options = installation.Value;
            if (options.Setup || !cluster.Value.Enabled || cluster.Value.Compatibility != OnboardingProtocol.TRUST_COMPATIBILITY
                || string.IsNullOrWhiteSpace(options.StateDirectory) || string.IsNullOrWhiteSpace(options.KeyDirectory)
                || string.IsNullOrWhiteSpace(options.SecretDirectory))
            {
                throw new InvalidOperationException("IssuerStorageConfigurationRequired");
            }

            if (ownedLease is null)
            {
                if (!create && (!File.Exists(Path.Combine(options.StateDirectory, "install.id"))
                    || !File.Exists(Path.Combine(options.SecretDirectory, "wrapping.key"))))
                {
                    throw new InvalidDataException("IssuerStorageMissing");
                }

                ownedLease = new(options, cluster.Value.StoragePath);
                ownedSecrets = new(installation, ownedLease);
            }

            if (create)
            {
                ownedSecrets!.Initialize();
            }

            return (ownedLease, ownedSecrets!);
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            ownedLease?.Dispose();
            ownedLease = null;
            ownedSecrets = null;
        }
    }
}
