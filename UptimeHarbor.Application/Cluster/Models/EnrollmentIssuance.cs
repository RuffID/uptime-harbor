namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Точный разрешённый leaf template. Сам по себе не является сертификатом или peer authority.</summary>
public record EnrollmentIssuance(int Version, Guid IssueId, Guid IssuerEpoch, string Serial,
    Guid NodeId, Guid StorageGeneration, string PeerUri, string SpkiDigest, string CsrDigest,
    DateTimeOffset NotBeforeUtc, DateTimeOffset NotAfterUtc)
{
    public override string ToString()
    {
        return $"EnrollmentIssuance {IssueId}";
    }
}
