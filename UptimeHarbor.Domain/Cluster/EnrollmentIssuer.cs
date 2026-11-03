namespace UptimeHarbor.Domain.Cluster;

/// <summary>Публичная принятая регистрация специализированного issuer; приватного ключа здесь нет.</summary>
public class EnrollmentIssuer : ProjectionRecord
{
    public const string PEER_PURPOSE = "UptimeHarbor.EnrollmentPeer.v1";

    private EnrollmentIssuer()
    {
    }

    public EnrollmentIssuer(Guid epoch, JournalPosition position, Guid nodeId, Guid storageGeneration,
        string publicCertificate, string fingerprint, string spkiDigest,
        DateTimeOffset notBeforeUtc, DateTimeOffset notAfterUtc)
        : base(epoch, position)
    {
        StorageGuards.Id(nodeId, nameof(nodeId));
        StorageGuards.Id(storageGeneration, nameof(storageGeneration));
        StorageGuards.Utc(notBeforeUtc);
        StorageGuards.Utc(notAfterUtc);
        if (notAfterUtc <= notBeforeUtc || notAfterUtc > notBeforeUtc.AddYears(3))
        {
            throw new ArgumentException("EnrollmentIssuerLifetimeInvalid");
        }

        NodeId = nodeId;
        StorageGeneration = storageGeneration;
        PublicCertificate = StorageGuards.PublicCertificate(publicCertificate);
        Fingerprint = StorageGuards.Digest(fingerprint);
        SpkiDigest = StorageGuards.Digest(spkiDigest);
        NotBeforeUtc = notBeforeUtc;
        NotAfterUtc = notAfterUtc;
    }

    public Guid NodeId { get; private set; }
    public Guid StorageGeneration { get; private set; }
    public string Purpose { get; private set; } = PEER_PURPOSE;
    public string PublicCertificate { get; private set; } = string.Empty;
    public string Fingerprint { get; private set; } = string.Empty;
    public string SpkiDigest { get; private set; } = string.Empty;
    public DateTimeOffset NotBeforeUtc { get; private set; }
    public DateTimeOffset NotAfterUtc { get; private set; }
    public DateTimeOffset? RetiredAtUtc { get; private set; }
    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public void Retire(DateTimeOffset atUtc)
    {
        ValidateTransitionTime(atUtc);
        RetiredAtUtc ??= atUtc;
    }

    public void Revoke(DateTimeOffset atUtc)
    {
        ValidateTransitionTime(atUtc);
        RevokedAtUtc ??= atUtc;
    }

    private void ValidateTransitionTime(DateTimeOffset atUtc)
    {
        StorageGuards.Utc(atUtc);
        if (atUtc < NotBeforeUtc)
        {
            throw new ArgumentException("EnrollmentIssuerTransitionTimeInvalid");
        }
    }

    public override string ToString()
    {
        return $"EnrollmentIssuer {Id}";
    }
}
