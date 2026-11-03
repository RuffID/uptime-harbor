namespace UptimeHarbor.Application.Cluster.Onboarding;

/// <summary>Порт issuer этапа 04: неизменяемый DER по IssueId сохраняется до внешнего ответа.</summary>
public interface IEnrollmentIssuanceStore
{
    Task<byte[]?> FindAsync(Guid issueId, CancellationToken token);
    Task SaveOnceAsync(Guid issueId, ReadOnlyMemory<byte> certificateDer, CancellationToken token);
}
