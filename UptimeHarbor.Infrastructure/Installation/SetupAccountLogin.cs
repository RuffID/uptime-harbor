using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Persistence;

namespace UptimeHarbor.Infrastructure.Installation;

public class SetupAccountLogin(ILocalSetupUnitOfWork work, ILocalSetupAccountStore accounts,
    InstallationLease lease, InstallationState state, TimeProvider time, ILogger<SetupAccountLogin> logger)
{
    public async Task<ServiceResult<SetupSession>> LoginAsync(string login, string password, CancellationToken token)
    {
        await state.Transition.WaitAsync(token);
        try
        {
            LocalInstallationProfile? profile = await work.Profiles.ReadAsync(token);
            LocalSetupAccount? account = await accounts.ReadAsync(token);
            if (profile is null || account is null || !InstallationTransitions.SetupOpen(profile.Phase)
                || profile.BootId != lease.BootId || account.Disabled || account.PasswordHash is null)
            {
                return Denied("SetupLoginRejected");
            }

            DateTimeOffset now = time.GetUtcNow();
            if (account.LockoutUntilUtc > now)
            {
                return Denied("SetupLoginLocked");
            }

            PasswordHasher<LocalSetupAccount> hasher = new();
            bool matches = hasher.VerifyHashedPassword(account, account.PasswordHash, password) != PasswordVerificationResult.Failed;
            matches = matches && account.NormalizedLogin == login.Trim().ToUpperInvariant();
            if (!matches)
            {
                account.FailedCount++;
                if (account.FailedCount >= 5)
                {
                    account.FailedCount = 0;
                    account.LockoutUntilUtc = now.AddMinutes(5);
                }

                account.Revision++;
                await work.SaveChangesAsync(token);
                if (account.LockoutUntilUtc > now)
                {
                    return Denied("SetupLoginLocked");
                }

                return Denied("SetupLoginRejected");
            }

            account.FailedCount = 0;
            account.LockoutUntilUtc = null;
            account.Revision++;
            await work.SaveChangesAsync(token);
            logger.LogInformation("{EventName} {ActorId} {InstallIdentity}", "SetupLoginAccepted", account.Id, profile.InstallIdentity);
            return ServiceResult<SetupSession>.Ok(new(account.Id, profile.InstallIdentity,
                account.Stamp, profile.SetupEpoch, lease.BootId, now));
        }
        finally
        {
            state.Transition.Release();
        }
    }

    public async Task<bool> ValidateAsync(SetupSession session, CancellationToken token)
    {
        LocalInstallationProfile? profile = await work.Profiles.ReadAsync(token);
        if (profile is null || session.InstallIdentity != lease.InstallIdentity || session.BootId != lease.BootId
            || profile.BootId != lease.BootId || session.Epoch != profile.SetupEpoch
            || !InstallationTransitions.SetupOpen(profile.Phase))
        {
            return false;
        }

        TimeSpan age = time.GetUtcNow() - session.StartedAtUtc;
        if (age < TimeSpan.Zero || age >= TimeSpan.FromMinutes(30))
        {
            return false;
        }

        LocalSetupAccount? account = await accounts.ReadAsync(token);
        return account is not null && !account.Disabled && account.PasswordHash is not null
            && account.Id == session.AccountId && account.CreatedForInstallIdentity == session.InstallIdentity
            && account.Stamp == session.Stamp;
    }

    private ServiceResult<SetupSession> Denied(string eventName)
    {
        logger.LogWarning("{EventName} {InstallIdentity}", eventName, lease.InstallIdentity);
        return ServiceResult<SetupSession>.Fail(ServiceErrorType.Unauthorized, "Вход недоступен. Проверьте данные или повторите позже.");
    }
}
