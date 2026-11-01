using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Users;
using UptimeHarbor.Application.Users.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Identity;

/// <summary>Совместимое чтение accepted accounts; локальные Identity writers в сетке запрещены.</summary>
public class AcceptedUserAccountStore(RevisionStateStore state) : IUserAccountStore
{
    public Task<IReadOnlyList<UserAccountRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!state.Available)
        {
            throw new InvalidOperationException("AcceptedStateUnavailable");
        }

        IReadOnlyList<UserAccountRecord> users = state.Current!.Values.Values
            .Where(value => value.Kind == DeletedEntityKind.Account && !value.Deleted)
            .Select(value => new UserAccountRecord(value.Id, value.Value.Name!, value.Value.Enabled, true)).ToArray();
        return Task.FromResult(users);
    }

    public async Task<UserAccountRecord?> GetByIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<UserAccountRecord> accounts = await GetAllAsync(cancellationToken);
        return accounts.SingleOrDefault(account => account.Id == userId);
    }

    public async Task<bool> HasOtherEnabledAdministratorAsync(Guid excludedUserId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<UserAccountRecord> accounts = await GetAllAsync(cancellationToken);
        return accounts.Any(account => account.Id != excludedUserId && account.IsEnabled);
    }

    public Task<AccountStoreResult> CreateAsync(Guid userId, string login, string initialPassword,
        CancellationToken cancellationToken = default)
    {
        return Denied();
    }

    public Task<AccountStoreResult> SetEnabledAsync(Guid userId, bool isEnabled, CancellationToken cancellationToken = default)
    {
        return Denied();
    }

    public Task<AccountStoreResult> DeleteAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return Denied();
    }

    private static Task<AccountStoreResult> Denied()
    {
        return Task.FromResult(AccountStoreResult.Failure(AccountStoreFailureKind.Conflict,
            ["Изменение общего аккаунта требует команды с OperationId и базовой версией."]));
    }
}
