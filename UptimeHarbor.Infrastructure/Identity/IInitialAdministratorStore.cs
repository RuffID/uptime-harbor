using UptimeHarbor.Application.Users.Models;

namespace UptimeHarbor.Infrastructure.Identity;

public interface IInitialAdministratorStore
{
    Task<bool> AdministratorExistsAsync(CancellationToken cancellationToken = default);

    Task<AccountStoreResult> EnsureRoleAsync(string roleName, CancellationToken cancellationToken = default);

    Task<AccountStoreResult> CreateAdministratorAsync(
        Guid userId,
        string login,
        string password,
        CancellationToken cancellationToken = default);
}
