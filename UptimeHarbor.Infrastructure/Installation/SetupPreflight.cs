using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Infrastructure.Installation;

public class SetupPreflight(IInstallationInventory inventory, IOptions<InstallationOptions> installation,
    IOptions<ClusterOptions> cluster, PreparedPeerEndpoint endpoint, InstallationLease lease)
{
    private static readonly HashSet<string> LOCAL_TABLES =
    [
        "local_setup_accounts", "local_installation_profiles", "local_onboarding_attempts", "local_attempt_steps",
        "data_protection_keys"
    ];

    public async Task<ServiceResult> CheckAsync(LocalInstallationProfile? profile, CancellationToken token)
    {
        InstallationInventory state = await inventory.ReadAsync(token);
        if (!state.SchemaCurrent)
        {
            return Reject("SchemaPreparationRequired");
        }

        if (state.NonemptyTables.Any(item => item.Value && !LOCAL_TABLES.Contains(item.Key)))
        {
            return Reject("NonemptyInstallationUseMigration03a03b");
        }

        if (profile is not null && profile.InstallIdentity != lease.InstallIdentity)
        {
            return Reject("InstallationIdentityMismatch");
        }

        ClusterOptions options = cluster.Value;
        try
        {
            new SetupPeerAddressPolicy(installation.Value).ValidateUri(installation.Value.PeerEndpoint);
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException)
        {
            return Reject("PeerAddressPolicyRejected");
        }

        if (!endpoint.Prepared || endpoint.Address != installation.Value.PeerEndpoint || options.TlsBoundary != "Direct")
        {
            return Reject("PreparedDirectPeerEndpointRequired");
        }

        if (options.Compatibility != "2.0/2/2/2/1/6.10.0")
        {
            return Reject("OnboardingVersionMismatch");
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(options.StoragePath))
        {
            if (Path.GetFileName(entry) != "installation.lock")
            {
                return Reject("ExistingClusterStorageForbidden");
            }
        }

        foreach (string path in new[] { installation.Value.StateDirectory, installation.Value.SecretDirectory,
            installation.Value.KeyDirectory, options.StoragePath })
        {
            ProtectedInstallationFiles.VerifyPermissions(path, true);
            string root = Path.GetPathRoot(path) ?? throw new InvalidDataException("InstallationVolumeInvalid");
            if (new DriveInfo(root).AvailableFreeSpace < 67108864)
            {
                return Reject("InstallationStorageFull");
            }
        }

        return ServiceResult.Ok();
    }

    private static ServiceResult Reject(string reason)
    {
        return ServiceResult.Fail(ServiceErrorType.Conflict, reason);
    }
}
