using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;

namespace UptimeHarbor.Infrastructure.Persistence.UnitOfWork;

public class OnboardingProgressUnitOfWork(IUptimeHarborDbContextSession session, ILocalOnboardingAttemptRepository attempts)
    : UptimeHarborDbUnitOfWork(session), IOnboardingProgressUnitOfWork
{
    public ILocalOnboardingAttemptRepository Attempts { get; } = attempts;
}
