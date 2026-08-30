using UptimeHarbor.Application.Users.Models;

namespace UptimeHarbor.Infrastructure.Identity;

public interface IInitialAdministratorStore
{
    Task<IReadOnlyList<UserAccountRecord>> GetAdministratorsAsync(
        CancellationToken cancellationToken = default);

    Task<AccountStoreResult> EnsureRoleAsync(string roleName, CancellationToken cancellationToken = default);

    Task<AccountStoreResult> CreateAdministratorAsync(
        Guid userId,
        string login,
        string password,
        CancellationToken cancellationToken = default);

    Task<AccountStoreResult> EnableAdministratorAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}
