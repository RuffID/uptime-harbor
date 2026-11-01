namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Локальная операционная аутентификация; приватный ключ разработчика хранится вне узлов.</summary>
public class ClusterControlOptions
{
    public string Directory { get; set; } = string.Empty;
    public string PublicKeyPath { get; set; } = string.Empty;
    public string PublicKeyDigest { get; set; } = string.Empty;
    public string KeyRingDirectory { get; set; } = string.Empty;
}
