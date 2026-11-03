namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Локальная привязка issuer к зашифрованному ключу. Не является accepted authority.</summary>
public class LocalEnrollmentIssuerRecord
{
    public Guid Id { get; set; }
    public Guid InstallIdentity { get; set; }
    public Guid ClusterId { get; set; }
    public long ClusterGeneration { get; set; }
    public Guid NodeId { get; set; }
    public Guid StorageGeneration { get; set; }
    public string KeyReference { get; set; } = string.Empty;
    public byte[] CertificateDer { get; set; } = [];
    public string CertificateDigest { get; set; } = string.Empty;
    public string SpkiDigest { get; set; } = string.Empty;
}
