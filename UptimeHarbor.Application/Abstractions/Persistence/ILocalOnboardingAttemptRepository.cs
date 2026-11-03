using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Abstractions.Persistence;

/// <summary>Локальный intent с CAS по revision; не часть общей projection или snapshot.</summary>
public interface ILocalOnboardingAttemptRepository
{
    Task<LocalOnboardingAttempt?> FindAsync(Guid attemptId, CancellationToken token);
    void Create(LocalOnboardingAttempt attempt);
    Task ReplaceAsync(LocalOnboardingAttempt attempt, long expectedRevision, CancellationToken token);
}
