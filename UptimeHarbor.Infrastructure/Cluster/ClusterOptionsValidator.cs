using System.Net;
using Microsoft.Extensions.Options;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Cluster;

public class ClusterOptionsValidator : IValidateOptions<ClusterOptions>
{
    public ValidateOptionsResult Validate(string? name, ClusterOptions options)
    {
        if (options.Mode is not ("Disabled" or "Bootstrap" or "Join" or "Restart"))
        {
            return ValidateOptionsResult.Fail("Cluster:Mode is invalid.");
        }

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        bool identityMissing = options.ClusterId == Guid.Empty || options.NodeId == Guid.Empty
            || options.StorageGeneration == Guid.Empty || options.CredentialId == Guid.Empty;
        if (identityMissing || options.Generation < 1)
        {
            return ValidateOptionsResult.Fail("Cluster identity is required.");
        }

        if (!Uri.TryCreate(options.PublicUri, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.AbsolutePath != "/internal/raft"
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsoluteUri != options.PublicUri)
        {
            return ValidateOptionsResult.Fail("Cluster:PublicUri must be canonical HTTPS /internal/raft.");
        }

        bool invalidTiming = options.HeartbeatSeconds != 5 || options.HealthTimeoutSeconds != 3
            || options.FailureWindowSeconds != 30 || options.HealthFreshSeconds != 10;
        bool invalidTransfer = options.CommandTimeoutSeconds != 10 || options.ChunkTimeoutSeconds != 30
            || options.ConfigurationIntervalSeconds is < 60 or > 3600
            || options.ConfigurationIntervalSeconds % 60 != 0;
        if (invalidTiming || invalidTransfer)
        {
            return ValidateOptionsResult.Fail("Cluster timing must match protocol 1.0.");
        }

        if (options.SnapshotIntervalSeconds is < 60 or > 3600)
        {
            return ValidateOptionsResult.Fail("Cluster snapshot interval is invalid.");
        }

        if (options.Compatibility != "1.0/1/1/1/1/6.10.0" && options.Compatibility != OnboardingProtocol.COMPATIBILITY)
        {
            return ValidateOptionsResult.Fail("Cluster compatibility is unsupported.");
        }

        bool invalidLimits = options.HealthMaxBytes is < 1 or > 65536
            || options.CommandMaxBytes is < 1 or > 262144 || options.BatchMaxBytes is < 1 or > 1048576
            || options.BatchMaxEntries is < 1 or > 100 || options.SnapshotChunkMaxBytes is < 16384 or > 1048576;
        if (invalidLimits || options.NativeMaxBytes < options.BatchMaxBytes
            || options.NativeMaxBytes > 67108864 || options.ReplayCapacity is < 100 or > 100000)
        {
            return ValidateOptionsResult.Fail("Cluster payload limits are invalid.");
        }

        if (!Path.IsPathFullyQualified(options.StoragePath)
            || !Path.IsPathFullyQualified(options.ClientCertificatePath)
            || !Path.IsPathFullyQualified(options.ClientKeyPath)
            || !Path.IsPathFullyQualified(options.ClientCaPath)
            || !IsFingerprint(options.ClientCaFingerprint) || options.ExpectedBotId <= 0
            || options.PeerListenPort is < 1 or > 65535)
        {
            return ValidateOptionsResult.Fail("Cluster storage, certificates, listener and BotId are required.");
        }

        if (options.TlsBoundary is not ("Direct" or "NginxIsolatedHttp"))
        {
            return ValidateOptionsResult.Fail("Cluster TLS boundary is invalid.");
        }

        if (options.TlsBoundary == "NginxIsolatedHttp" && options.TrustedProxyAddresses.Length == 0)
        {
            return ValidateOptionsResult.Fail("Cluster isolated ingress requires explicit proxy addresses.");
        }

        foreach (string proxy in options.TrustedProxyAddresses)
        {
            if (!IPAddress.TryParse(proxy, out _))
            {
                return ValidateOptionsResult.Fail("Cluster proxy address is invalid.");
            }
        }

        return ValidateOptionsResult.Success;
    }

    public static bool IsFingerprint(string value)
    {
        return value.Length == 64 && value.All(Uri.IsHexDigit);
    }
}
