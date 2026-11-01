using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Users;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Identity;

/// <summary>Сценарный UoW локального lockout без UserManager.Update и изменения shared stamps.</summary>
public class LocalAccountLogin(ILocalLoginUnitOfWork work,
    IAccountCredentialService credentials, IOptions<IdentityOptions> options, TimeProvider time,
    LocalAccountLoginGate gate) : ILocalAccountLogin
{
    public async Task<bool> CheckAsync(SharedAccount account, string password, CancellationToken token)
    {
        return await gate.ExecuteAsync(() => CheckCoreAsync(account, password, token), token);
    }

    private async Task<bool> CheckCoreAsync(SharedAccount account, string password, CancellationToken token)
    {
        ApplicationUser? local = await work.FindAsync(account.UserId, token);
        if (local is null || !local.IsEnabled || local.DeletedAtUtc.HasValue
            || local.UserRevision != account.UserRevision || local.PasswordHash != account.Credential.PasswordHash
            || local.SecurityStamp != account.Credential.SecurityStamp)
        {
            return false;
        }

        DateTimeOffset now = time.GetUtcNow();
        if (local.LockoutEnd > now)
        {
            return false;
        }

        bool valid = credentials.Verify(account.UserId, account.Login, account.Credential, password);
        if (valid)
        {
            local.AccessFailedCount = 0;
            local.LockoutEnd = null;
        }
        else
        {
            local.AccessFailedCount++;
            if (local.AccessFailedCount >= options.Value.Lockout.MaxFailedAccessAttempts)
            {
                local.AccessFailedCount = 0;
                local.LockoutEnd = now.Add(options.Value.Lockout.DefaultLockoutTimeSpan);
            }
        }

        try
        {
            await work.SaveChangesAsync(token);
        }
        catch (PersistenceConcurrencyException)
        {
            // Apply credentials выиграл гонку: вход по прочитанной версии запрещён.
            return false;
        }

        return valid;
    }
}
