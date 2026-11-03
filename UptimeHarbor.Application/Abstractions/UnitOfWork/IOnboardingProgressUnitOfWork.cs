using UptimeHarbor.Application.Abstractions.Persistence;

namespace UptimeHarbor.Application.Abstractions.UnitOfWork;

/// <summary>Сохранение локального шага до сети и удаление protected code после принятого reserve.</summary>
public interface IOnboardingProgressUnitOfWork : IUnitOfWorkScope
{
    ILocalOnboardingAttemptRepository Attempts { get; }
}
