using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Identity;

/// <summary>Проверка пароля и запись только локальных counters; не изменяет общие поля.</summary>
public interface ILocalAccountLogin
{
    Task<bool> CheckAsync(SharedAccount account, string password, CancellationToken token);
}
