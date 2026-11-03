using UptimeHarbor.Application.Installation;

namespace UptimeHarbor.Infrastructure.Persistence.UnitOfWork;

public class LocalSetupUnitOfWork(IUptimeHarborDbContextSession session, ILocalInstallationRepository profiles)
    : UptimeHarborDbUnitOfWork(session), ILocalSetupUnitOfWork
{
    public ILocalInstallationRepository Profiles { get; } = profiles;
}
