namespace UptimeHarbor.Infrastructure.Cluster;

public class ClusterOptions
{
    public const string SECTION_NAME = "Cluster";
    public const string DOTNEXT_VERSION = "6.10.0";
    public string Mode { get; set; } = "Disabled";
    public Guid ClusterId { get; set; }
    public long Generation { get; set; }
    public Guid NodeId { get; set; }
    public Guid StorageGeneration { get; set; }
    public Guid CredentialId { get; set; }
    public string PublicUri { get; set; } = string.Empty;
    public string StoragePath { get; set; } = string.Empty;
    public int PeerListenPort { get; set; } = 8443;
    public string TlsBoundary { get; set; } = "Direct";
    public string[] TrustedProxyAddresses { get; set; } = [];
    public string ClientCertificatePath { get; set; } = string.Empty;
    public string ClientKeyPath { get; set; } = string.Empty;
    public string ClientCaPath { get; set; } = string.Empty;
    public string ClientCaFingerprint { get; set; } = string.Empty;
    public int HeartbeatSeconds { get; set; } = 5;
    public int HealthTimeoutSeconds { get; set; } = 3;
    public int FailureWindowSeconds { get; set; } = 30;
    public int HealthFreshSeconds { get; set; } = 10;
    public int CommandTimeoutSeconds { get; set; } = 10;
    public int ChunkTimeoutSeconds { get; set; } = 30;
    public int ConfigurationIntervalSeconds { get; set; } = 60;
    public int SnapshotIntervalSeconds { get; set; } = 300;
    public int ProtocolMajor { get; set; } = 1;
    public int ProtocolMinor { get; set; }
    public int CommandSchema { get; set; } = 1;
    public int SnapshotFormat { get; set; } = 1;
    public int SqlSchema { get; set; } = 1;
    public int PasswordHashFormat { get; set; } = 1;
    public int HealthMaxBytes { get; set; } = 65536;
    public int CommandMaxBytes { get; set; } = 262144;
    public int BatchMaxBytes { get; set; } = 1048576;
    public int BatchMaxEntries { get; set; } = 100;
    public int SnapshotChunkMaxBytes { get; set; } = 1048576;
    public long NativeMaxBytes { get; set; } = 67108864;
    public int ReplayCapacity { get; set; } = 10000;
    public long ExpectedBotId { get; set; }
    public bool Enabled => Mode != "Disabled";

    public string Compatibility => $"{ProtocolMajor}.{ProtocolMinor}/{CommandSchema}/{SnapshotFormat}/{SqlSchema}/{PasswordHashFormat}/{DOTNEXT_VERSION}";
}
