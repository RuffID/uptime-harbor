using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Infrastructure.Installation;

public class SetupPreflight(IInstallationInventory inventory, IOptions<InstallationOptions> installation,
    IOptions<ClusterOptions> cluster, PreparedPeerEndpoint endpoint, InstallationLease lease,
    ManagedStorageSeals? seals = null)
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

        bool restart = profile?.Phase is InstallationPhase.ClusterReady or InstallationPhase.ClusterQuarantine
            or InstallationPhase.CatchingUp or InstallationPhase.AwaitingActivation;
        if (restart)
        {
            if (seals is null || !seals.Exists)
            {
                return Reject("ManagedStorageSealRequired");
            }

            seals.Read(profile!);
        }

        bool signerIntent = profile?.Mode == "Bootstrap" && profile.AttemptId.HasValue && profile.Evidence is not null;
        if (!restart && state.NonemptyTables.Any(item => item.Value && !LOCAL_TABLES.Contains(item.Key)
            && !(signerIntent && item.Key is "local_enrollment_issuers" or "local_enrollment_issuances")))
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

        if (options.Compatibility != "2.0/2/2/2/1/6.10.0"
            && options.Compatibility != UptimeHarbor.Infrastructure.Cluster.Onboarding.OnboardingProtocol.TRUST_COMPATIBILITY)
        {
            return Reject("OnboardingVersionMismatch");
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(options.StoragePath))
        {
            if (!restart && Path.GetFileName(entry) != "installation.lock"
                && !(profile?.Mode == "Bootstrap" && profile.Evidence is not null && Path.GetFileName(entry) == "manifest.json"))
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
