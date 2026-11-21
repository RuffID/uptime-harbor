namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

/// <summary>Серверный контекст общей аутентификации. Для локальной setup-сессии возвращает null.</summary>
public interface IVoterContinuationAdministrator
{
    Guid? CommonActorId { get; }
}
