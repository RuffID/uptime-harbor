namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Локальная ссылка на issuer material, никогда не DTO браузера или snapshot.</summary>
public record EnrollmentIssuerReference(Guid Epoch, Guid InstallIdentity, Guid ClusterId, long Generation,
    Guid NodeId, Guid StorageGeneration, string KeyReference, byte[] CertificateDer, string CertificateDigest, string SpkiDigest)
{
    public override string ToString()
    {
        return $"EnrollmentIssuerReference {Epoch}";
    }
}
