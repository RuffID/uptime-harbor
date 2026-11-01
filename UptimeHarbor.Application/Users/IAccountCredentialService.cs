using UptimeHarbor.Application.Common;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Users;

/// <summary>Подготовка хеша до durable записи; проверка не обновляет принятые credentials.</summary>
public interface IAccountCredentialService
{
    Task<ServiceResult<ProtectedCredential>> PrepareAsync(Guid userId, string login, string password,
        CancellationToken cancellationToken);
    bool Verify(Guid userId, string login, ProtectedCredential credential, string password);
}
