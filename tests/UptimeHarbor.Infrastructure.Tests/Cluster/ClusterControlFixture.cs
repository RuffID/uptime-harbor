using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Models;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

internal class ClusterControlFixture : IDisposable
{
    public ClusterTestDirectory Directory { get; } = new();
    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public MonitoringHarness.Clock Time { get; } = new() { Now = DateTimeOffset.UtcNow };
    public ClusterOptions Options { get; }
    public ClusterControlOptions Configuration { get; }
    public NodeReadiness Readiness { get; }
    public ClusterLocalControl Control { get; }

    public ClusterControlFixture()
    {
        Options = new()
        {
            Mode = "Bootstrap", ClusterId = Guid.NewGuid(), Generation = 1, NodeId = Guid.NewGuid(),
            StorageGeneration = Guid.NewGuid(), CredentialId = Guid.NewGuid(),
            StoragePath = Path.Combine(Directory.Location, "storage"), PublicUri = "https://first.invalid/internal/raft", ExpectedBotId = 123
        };
        Configuration = new()
        {
            Directory = Path.Combine(Directory.Location, "control"), PublicKeyPath = Path.Combine(Directory.Location, "operator.pem"),
            PublicKeyDigest = Convert.ToHexString(SHA256.HashData(Key.ExportSubjectPublicKeyInfo()))
        };
        File.WriteAllText(Configuration.PublicKeyPath, Key.ExportSubjectPublicKeyInfoPem());
        System.IO.Directory.CreateDirectory(Configuration.Directory);
        Readiness = new(Time);
        Control = NewControl();
    }

    public ClusterLocalControl NewControl()
    {
        return new(Microsoft.Extensions.Options.Options.Create(Options), Microsoft.Extensions.Options.Options.Create(Configuration), Time, Readiness);
    }

    public ClusterControlRequest Request(string action, ClusterLocalControl? control = null)
    {
        return new(Guid.NewGuid(), (control ?? Control).Challenge, Options.ClusterId, Options.Generation,
            Options.NodeId, Options.StorageGeneration, action, Time.Now, Time.Now.AddMinutes(10))
        {
            RecoveryVersion = Readiness.RecoveryVersion
        };
    }

    public void Sign(string file, ClusterControlRequest request)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request);
        SignedClusterControl signed = new(Convert.ToBase64String(payload), Convert.ToBase64String(Key.SignData(payload, HashAlgorithmName.SHA256)));
        File.WriteAllBytes(Path.Combine(Configuration.Directory, file), JsonSerializer.SerializeToUtf8Bytes(signed));
    }

    public void Dispose()
    {
        Key.Dispose();
        Directory.Dispose();
    }
}
