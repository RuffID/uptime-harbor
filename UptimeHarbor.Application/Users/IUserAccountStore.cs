using UptimeHarbor.Application.Users.Models;

namespace UptimeHarbor.Application.Users;

public interface IUserAccountStore
{
    Task<IReadOnlyList<UserAccountRecord>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<UserAccountRecord?> GetByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<AccountStoreResult> CreateAsync(
        Guid userId,
        string login,
        string initialPassword,
        CancellationToken cancellationToken = default);

    Task<AccountStoreResult> SetEnabledAsync(
        Guid userId,
        bool isEnabled,
        CancellationToken cancellationToken = default);

    Task<AccountStoreResult> DeleteAsync(Guid userId, CancellationToken cancellationToken = default);
}
