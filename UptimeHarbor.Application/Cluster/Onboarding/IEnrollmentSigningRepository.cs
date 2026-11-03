using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster.Onboarding;

/// <summary>Только локальные immutable записи signer, без общего реестра authority.</summary>
public interface IEnrollmentSigningRepository
{
    Task<IReadOnlyList<EnrollmentIssuerReference>> ListIssuersAsync(CancellationToken token);
    Task<EnrollmentIssuerReference?> FindIssuerAsync(Guid epoch, CancellationToken token);
    Task<EnrollmentIssuedArtifact?> FindIssueAsync(Guid issueId, CancellationToken token);
    void AddIssuer(EnrollmentIssuerReference issuer);
    void AddIssue(EnrollmentIssuedArtifact artifact);
}
