namespace UptimeHarbor.Domain.Cluster;

public class ClusterTrustRoot : ProjectionRecord
{
    private ClusterTrustRoot()
    {
    }

    public ClusterTrustRoot(Guid id, JournalPosition position, string fingerprint, string publicCertificate)
        : base(id, position)
    {
        Fingerprint = StorageGuards.Digest(fingerprint);
        PublicCertificate = StorageGuards.PublicCertificate(publicCertificate);
    }

    public string Fingerprint { get; private set; } = string.Empty;
    public string PublicCertificate { get; private set; } = string.Empty;
    public DateTimeOffset? RetiredAtUtc { get; private set; }

    public void Retire(DateTimeOffset retiredAtUtc)
    {
        StorageGuards.Utc(retiredAtUtc);
        RetiredAtUtc ??= retiredAtUtc;
    }
}
