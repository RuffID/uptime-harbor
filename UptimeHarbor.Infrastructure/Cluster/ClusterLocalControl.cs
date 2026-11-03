using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Models;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Проверяет подпись оператора, freshness и одноразовый challenge. Не утверждает внешнее fencing.</summary>
public class ClusterLocalControl(IOptions<ClusterOptions> cluster, IOptions<ClusterControlOptions> configured,
    TimeProvider time, NodeReadiness readiness, ManagedAdmissionState? online = null, OnlineEnableState? onlineEnable = null)
{
    private static readonly JsonSerializerOptions JSON = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };
    private readonly object sync = new();
    private readonly Guid challenge = Guid.NewGuid();
    private readonly HashSet<Guid> consumed = [];
    private ClusterControlRequest? enabled;
    private bool published;
    private Guid latchedMaintenance;
    private Guid resumedMaintenance;

    public Guid Challenge => challenge;
    public bool Configured => cluster.Value.Enabled && configured.Value.Directory.Length > 0;

    public void PublishStatus(long operationsRevision, Guid maintenanceId, string role, long? term, long? membershipVersion)
    {
        if (!Configured)
        {
            return;
        }

        ValidateOptions(configured.Value);
        PublishChallenge();
        string file = Path.Combine(configured.Value.Directory, "status.json");
        string temporary = file + ".tmp";
        using (FileStream output = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(output, new
            {
                Challenge = challenge, cluster.Value.ClusterId, cluster.Value.Generation,
                cluster.Value.NodeId, cluster.Value.StorageGeneration,
                RecoveryVersion = readiness.RecoveryVersion, OperationsRevision = operationsRevision,
                MaintenanceId = maintenanceId, Role = role, Term = term, MembershipVersion = membershipVersion,
                Database = readiness.Get("Database"), Projection = readiness.Get("Projection"),
                Storage = readiness.Get("Storage"), Clock = readiness.Get("Clock"), AtUtc = time.GetUtcNow()
            });
            output.Flush(true);
        }

        File.Move(temporary, file, true);
    }

    public string UnprotectBootstrap(string value)
    {
        string directory = configured.Value.KeyRingDirectory;
        if (!Path.IsPathFullyQualified(directory) || !System.IO.Directory.Exists(directory))
        {
            throw new InvalidDataException("LocalControlKeyRingMissing");
        }

        IDataProtectionProvider provider = DataProtectionProvider.Create(new DirectoryInfo(directory),
            builder => builder.SetApplicationName($"UptimeHarbor.ClusterControl/{cluster.Value.NodeId:N}"));
        return provider.CreateProtector("UptimeHarbor.Control.Bootstrap.v1").Unprotect(value);
    }

    public bool ActivationRequested(long operationsRevision)
    {
        ClusterControlRequest? request = Read("activate.json", "Activate");
        return request is not null && request.RecoveryVersion == readiness.RecoveryVersion
            && request.OperationsRevision == operationsRevision;
    }

    public bool CanRunConsensus(Guid maintenanceId)
    {
        lock (sync)
        {
            if (!Configured)
            {
                return online?.AllowsConsensus(maintenanceId) == true && latchedMaintenance == Guid.Empty;
            }

            if (File.Exists(Path.Combine(configured.Value.Directory, "blocked")))
            {
                return false;
            }

            if (maintenanceId != Guid.Empty && maintenanceId != latchedMaintenance)
            {
                LatchMaintenance(maintenanceId);
            }

            string marker = Path.Combine(configured.Value.Directory, "maintenance");
            if (!File.Exists(marker))
            {
                return true;
            }

            Guid persisted = JsonSerializer.Deserialize<Guid>(File.ReadAllBytes(marker));
            if (persisted != Guid.Empty && resumedMaintenance == persisted)
            {
                return true;
            }

            ClusterControlRequest? resume = Read("resume.json", "ResumeConsensus");
            if (resume is null || resume.MaintenanceId != persisted || persisted == Guid.Empty)
            {
                return false;
            }

            resumedMaintenance = persisted;
            return true;
        }
    }

    public bool CanLeaveMaintenance(Guid maintenanceId)
    {
        lock (sync)
        {
            return maintenanceId != Guid.Empty && resumedMaintenance == maintenanceId;
        }
    }

    public ClusterControlRequest? Read(string fileName, string action)
    {
        if (!Configured)
        {
            return null;
        }

        ClusterControlOptions options = configured.Value;
        ValidateOptions(options);
        PublishChallenge();
        if (File.Exists(Path.Combine(options.Directory, "blocked")))
        {
            return null;
        }

        string file = Path.Combine(options.Directory, fileName);
        if (!File.Exists(file))
        {
            return null;
        }

        using FileStream input = new(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > 524288)
        {
            throw new InvalidDataException("ControlLengthInvalid");
        }

        SignedClusterControl envelope = JsonSerializer.Deserialize<SignedClusterControl>(input, JSON)
            ?? throw new InvalidDataException("ControlEnvelopeMissing");
        byte[] payload = Convert.FromBase64String(envelope.Payload);
        byte[] signature = Convert.FromBase64String(envelope.Signature);
        using ECDsa authority = ECDsa.Create();
        authority.ImportFromPem(File.ReadAllText(options.PublicKeyPath));
        string digest = Convert.ToHexString(SHA256.HashData(authority.ExportSubjectPublicKeyInfo()));
        if (digest != options.PublicKeyDigest || !authority.VerifyData(payload, signature, HashAlgorithmName.SHA256))
        {
            throw new InvalidDataException("ControlSignatureInvalid");
        }

        ClusterControlRequest request = JsonSerializer.Deserialize<ClusterControlRequest>(payload, JSON)
            ?? throw new InvalidDataException("ControlRequestMissing");
        ClusterOptions node = cluster.Value;
        bool identity = request.ClusterId == node.ClusterId && request.Generation == node.Generation
            && request.NodeId == node.NodeId && request.StorageGeneration == node.StorageGeneration;
        DateTimeOffset now = time.GetUtcNow();
        bool fresh = request.IssuedAtUtc.Offset == TimeSpan.Zero && request.ExpiresAtUtc.Offset == TimeSpan.Zero
            && request.IssuedAtUtc <= now && request.ExpiresAtUtc > now
            && request.ExpiresAtUtc - request.IssuedAtUtc <= TimeSpan.FromMinutes(15);
        if (!identity || !fresh || request.Challenge != challenge || request.RequestId == Guid.Empty || request.Action != action)
        {
            return null;
        }

        return request;
    }

    public bool CanWork(long operationsRevision, Guid maintenanceId)
    {
        lock (sync)
        {
            if (online?.Read() is not null)
            {
                if (maintenanceId != Guid.Empty || latchedMaintenance != Guid.Empty || !online.AllowsConsensus(maintenanceId))
                {
                    return false;
                }

                return onlineEnable?.Allows() == true;
            }

            if (!Configured || File.Exists(Path.Combine(configured.Value.Directory, "blocked")))
            {
                enabled = null;
                return false;
            }

            if (enabled is not null && (enabled.RecoveryVersion != readiness.RecoveryVersion
                || maintenanceId != Guid.Empty || File.Exists(Path.Combine(configured.Value.Directory, "maintenance"))))
            {
                enabled = null;
            }

            if (enabled is not null)
            {
                return true;
            }

            ClusterControlRequest? request = Read("enable.json", "Enable");
            if (request is null || consumed.Contains(request.RequestId) || request.RecoveryVersion != readiness.RecoveryVersion
                || request.OperationsRevision != operationsRevision || request.MaintenanceId != maintenanceId
                || maintenanceId != Guid.Empty)
            {
                return false;
            }

            consumed.Add(request.RequestId);
            enabled = request;
            File.Delete(Path.Combine(configured.Value.Directory, "maintenance"));
            return true;
        }
    }

    public void LatchMaintenance(Guid maintenanceId)
    {
        lock (sync)
        {
            enabled = null;
            latchedMaintenance = maintenanceId;
            resumedMaintenance = Guid.Empty;
            if (!Configured)
            {
                return;
            }

            PublishChallenge();
            string file = Path.Combine(configured.Value.Directory, "maintenance");
            using FileStream marker = new(file, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            JsonSerializer.Serialize(marker, maintenanceId);
            marker.Flush(true);
        }
    }

    private void PublishChallenge()
    {
        lock (sync)
        {
            if (published)
            {
                return;
            }

            System.IO.Directory.CreateDirectory(configured.Value.Directory);
            string file = Path.Combine(configured.Value.Directory, "challenge.json");
            using FileStream output = new(file, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            JsonSerializer.Serialize(output, new { Challenge = challenge, cluster.Value.ClusterId, cluster.Value.Generation,
                cluster.Value.NodeId, cluster.Value.StorageGeneration });
            output.Flush(true);
            published = true;
        }
    }

    private static void ValidateOptions(ClusterControlOptions options)
    {
        if (!Path.IsPathFullyQualified(options.Directory) || !Path.IsPathFullyQualified(options.PublicKeyPath)
            || !ClusterOptionsValidator.IsFingerprint(options.PublicKeyDigest))
        {
            throw new InvalidDataException("ControlConfigurationInvalid");
        }
    }
}
