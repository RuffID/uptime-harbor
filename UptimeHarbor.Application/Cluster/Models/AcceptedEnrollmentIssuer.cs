namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Публичная accepted registration. Epoch не восстанавливается из подписи CA.</summary>
public record AcceptedEnrollmentIssuer(Guid Epoch, Guid NodeId, Guid StorageGeneration, string Purpose,
    string CertificateDer, string CertificateDigest, string SpkiDigest,
    DateTimeOffset NotBeforeUtc, DateTimeOffset NotAfterUtc, long Revision,
    DateTimeOffset? RetiredAtUtc = null, DateTimeOffset? RevokedAtUtc = null)
{
    public override string ToString()
    {
        return $"EnrollmentIssuer {Epoch}";
    }
}
