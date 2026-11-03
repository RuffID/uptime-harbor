using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Tests.Web.Cluster;

/// <summary>Durable fake с CAS, без EF provider и БД.</summary>
public class EnrollmentClientStore : IOnboardingProgressUnitOfWork, ILocalOnboardingAttemptRepository
{
    public LocalOnboardingAttempt? Current { get; private set; }
    public ILocalOnboardingAttemptRepository Attempts => this;

    public Task<LocalOnboardingAttempt?> FindAsync(Guid attemptId, CancellationToken token)
    {
        return Task.FromResult(Current);
    }

    public void Create(LocalOnboardingAttempt attempt)
    {
        if (Current is not null)
        {
            throw new InvalidOperationException("TestAttemptAlreadyExists");
        }

        Current = attempt;
    }

    public Task ReplaceAsync(LocalOnboardingAttempt attempt, long expectedRevision, CancellationToken token)
    {
        if (Current?.Revision != expectedRevision || attempt.Revision != expectedRevision + 1)
        {
            throw new InvalidOperationException("TestAttemptRevisionChanged");
        }

        Current = attempt;
        return Task.CompletedTask;
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(1);
    }

    public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        await action(cancellationToken);
        await SaveChangesAsync(cancellationToken);
    }
}
