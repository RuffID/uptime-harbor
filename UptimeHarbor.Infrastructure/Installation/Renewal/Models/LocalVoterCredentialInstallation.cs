namespace UptimeHarbor.Infrastructure.Installation.Renewal.Models;

/// <summary>Подтверждённая локальная установка. Допуск runtime проверяется отдельно.</summary>
public record LocalVoterCredentialInstallation(string MaterialReference, Guid IssueId,
    long CredentialRevision, string CertificateFingerprint);
