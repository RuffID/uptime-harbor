using UptimeHarbor.Application.Abstractions.UnitOfWork;

namespace UptimeHarbor.Application.Installation;

public interface ILocalSetupUnitOfWork : IUnitOfWorkScope
{
    ILocalInstallationRepository Profiles { get; }
}
