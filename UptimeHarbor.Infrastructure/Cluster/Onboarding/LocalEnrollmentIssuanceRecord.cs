namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Неизменяемые DER по IssueId, сохранённые до ответа signer. Не operational credential.</summary>
public class LocalEnrollmentIssuanceRecord
{
    public Guid Id { get; set; }
    public Guid IssuerEpoch { get; set; }
    public Guid AttemptId { get; set; }
    public string RequestDigest { get; set; } = string.Empty;
    public string TemplateDigest { get; set; } = string.Empty;
    public Guid? GenesisId { get; set; }
    public long AuthorizationIndex { get; set; }
    public byte[] CertificateDer { get; set; } = [];
    public string CertificateDigest { get; set; } = string.Empty;
}
