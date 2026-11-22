using System.Net;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Cluster;

public class ClusterOptionsValidator : IValidateOptions<ClusterOptions>
{
    public ValidateOptionsResult Validate(string? name, ClusterOptions options)
    {
        if (options.Mode is not ("Disabled" or "Bootstrap" or "Join" or "Restart"))
        {
            return ValidateOptionsResult.Fail("Cluster:Mode must be Disabled, Bootstrap, Join or Restart.");
        }

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (options.NodeId == Guid.Empty)
        {
            return ValidateOptionsResult.Fail("Cluster:NodeId must be a non-empty UUID.");
        }

        if (options.ClusterId == Guid.Empty || options.StorageGeneration == Guid.Empty
            || options.CredentialId == Guid.Empty || options.Generation < 1)
        {
            return ValidateOptionsResult.Fail("Cluster:ClusterId, StorageGeneration and CredentialId must be non-empty UUIDs; Generation must be positive.");
        }

        string? rosterError = ValidateNodes(options);
        if (rosterError is not null)
        {
            return ValidateOptionsResult.Fail(rosterError);
        }

        if (!ClusterOperationValidation.IsValidAddress(options.PublicUri))
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

        if (options.Compatibility != "1.0/1/1/1/1/6.10.0" && options.Compatibility != OnboardingProtocol.COMPATIBILITY
            && options.Compatibility != OnboardingProtocol.TRUST_COMPATIBILITY)
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

        List<string> pathErrors = [];
        foreach ((string setting, string path) in new (string, string)[]
        {
            (nameof(options.StoragePath), options.StoragePath),
            (nameof(options.ClientCertificatePath), options.ClientCertificatePath),
            (nameof(options.ClientKeyPath), options.ClientKeyPath),
            (nameof(options.ClientCaPath), options.ClientCaPath)
        })
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                pathErrors.Add($"Cluster:{setting} must be an absolute path.");
            }
        }

        if (pathErrors.Count != 0)
        {
            return ValidateOptionsResult.Fail(pathErrors);
        }

        if (!IsFingerprint(options.ClientCaFingerprint))
        {
            return ValidateOptionsResult.Fail("Cluster:ClientCaFingerprint must contain 64 hexadecimal SHA-256 digits.");
        }

        if (options.ExpectedBotId <= 0 || options.PeerListenPort is < 1 or > 65535)
        {
            return ValidateOptionsResult.Fail("Cluster:ExpectedBotId must be positive and Cluster:PeerListenPort must be between 1 and 65535.");
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

    public static bool IsFingerprint(string? value)
    {
        return value is { Length: 64 } && value.All(Uri.IsHexDigit);
    }

    private static string? ValidateNodes(ClusterOptions options)
    {
        if (options.Nodes.Length == 0)
        {
            return null;
        }

        if (options.Nodes.Length > 3)
        {
            return "Cluster:Nodes supports up to three nodes.";
        }

        HashSet<int> numbers = [];
        HashSet<Guid> ids = [];
        HashSet<string> addresses = new(StringComparer.Ordinal);
        foreach (ClusterNodeOptions node in options.Nodes)
        {
            if (node is null)
            {
                return "Cluster:Nodes must contain a number, NodeId and Address for each entry.";
            }

            if (node.Number < 1 || node.Number > options.Nodes.Length || !numbers.Add(node.Number))
            {
                return "Cluster:Nodes numbers must be unique and consecutive starting from 1.";
            }

            if (node.NodeId == Guid.Empty || !ids.Add(node.NodeId))
            {
                return $"Cluster:Nodes entry #{node.Number} must have a unique non-empty NodeId.";
            }

            if (!ClusterOperationValidation.IsValidAddress(node.Address) || !addresses.Add(node.Address))
            {
                return $"Cluster:Nodes entry #{node.Number} must have a unique canonical HTTPS address ending in /internal/raft.";
            }
        }

        ClusterNodeOptions? local = options.FindNode(options.NodeId);
        if (local is null)
        {
            return "Cluster:NodeId must match a local entry in Cluster:Nodes.";
        }

        if (local.Address != options.PublicUri)
        {
            return "Cluster:PublicUri must match the address for Cluster:NodeId in Cluster:Nodes.";
        }

        return null;
    }
}
