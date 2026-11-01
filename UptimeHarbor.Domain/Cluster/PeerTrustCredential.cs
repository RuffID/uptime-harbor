namespace UptimeHarbor.Domain.Cluster;

public class PeerTrustCredential : ProjectionRecord
{
    private PeerTrustCredential()
    {
    }

    public PeerTrustCredential(Guid credentialId, JournalPosition position, Guid nodeId,
        string fingerprint, string publicCertificate, DateTimeOffset notAfterUtc,
        TrustPurpose purpose = TrustPurpose.Active, Guid? admissionId = null, DateTimeOffset? admissionExpiresAtUtc = null)
        : base(credentialId, position)
    {
        StorageGuards.Id(nodeId, nameof(nodeId));
        StorageGuards.Utc(notAfterUtc);
        bool incompleteAdmission = admissionId.HasValue != admissionExpiresAtUtc.HasValue;
        bool missingAdmission = purpose == TrustPurpose.Admission && !admissionId.HasValue;
        if (!Enum.IsDefined(purpose) || admissionId == Guid.Empty || incompleteAdmission || missingAdmission)
        {
            throw new ArgumentException("Invalid credential purpose or admission metadata.");
        }

        if (admissionExpiresAtUtc.HasValue)
        {
            StorageGuards.Utc(admissionExpiresAtUtc.Value);
        }

        NodeId = nodeId;
        Fingerprint = StorageGuards.Digest(fingerprint);
        PublicCertificate = StorageGuards.PublicCertificate(publicCertificate);
        NotAfterUtc = notAfterUtc;
        Purpose = purpose;
        AdmissionId = admissionId;
        AdmissionExpiresAtUtc = admissionExpiresAtUtc;
    }

    public Guid NodeId { get; private set; }
    public string Fingerprint { get; private set; } = string.Empty;
    public string PublicCertificate { get; private set; } = string.Empty;
    public DateTimeOffset NotAfterUtc { get; private set; }
    public DateTimeOffset? RevokedAtUtc { get; private set; }
    public TrustPurpose Purpose { get; private set; }
    public Guid? AdmissionId { get; private set; }
    public DateTimeOffset? AdmissionExpiresAtUtc { get; private set; }
    public DateTimeOffset? AdmissionConsumedAtUtc { get; private set; }

    public void ConsumeAdmission(DateTimeOffset consumedAtUtc)
    {
        StorageGuards.Utc(consumedAtUtc);
        if (Purpose != TrustPurpose.Admission || AdmissionConsumedAtUtc.HasValue
            || consumedAtUtc >= AdmissionExpiresAtUtc)
        {
            throw new InvalidOperationException("Admission is expired, consumed or unavailable.");
        }

        AdmissionConsumedAtUtc = consumedAtUtc;
    }

    public void Revoke(DateTimeOffset revokedAtUtc)
    {
        StorageGuards.Utc(revokedAtUtc);
        RevokedAtUtc ??= revokedAtUtc;
    }
}
