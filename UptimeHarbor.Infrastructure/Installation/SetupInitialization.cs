using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Infrastructure.Persistence;

namespace UptimeHarbor.Infrastructure.Installation;

public class SetupInitialization(ILocalSetupUnitOfWork work, ILocalSetupAccountStore accounts,
    IOptions<BootstrapAdminOptions> credentials, InstallationLease lease, SetupPreflight preflight,
    InstallationState state, LocalSecretStore secrets, TimeProvider time, ILogger<SetupInitialization> logger)
{
    public async Task InitializeAsync(CancellationToken token)
    {
        ServiceResult check = await preflight.CheckAsync(null, token);
        if (!check.Success)
        {
            logger.LogWarning("{EventName} {Reason}", "SetupPreflightRejected", check.Error!.Message);
            throw new InvalidDataException(check.Error.Message);
        }

        LocalInstallationProfile? profile = await work.Profiles.ReadAsync(token);
        if (profile is not null && profile.InstallIdentity != lease.InstallIdentity)
        {
            throw new InvalidDataException("InstallationIdentityMismatch");
        }

        secrets.ReconcileAll(profile);
        secrets.Initialize();

        if (profile is not null)
        {
            if (profile.BootId != lease.BootId && profile.LeaseUntilUtc > time.GetUtcNow())
            {
                await Task.Delay(profile.LeaseUntilUtc - time.GetUtcNow(), time, token);
                LocalInstallationProfile? verified = await work.Profiles.ReadAsync(token);
                if (verified != profile)
                {
                    throw new InvalidDataException("InstallationProcessLeaseChanged");
                }
            }

            await accounts.ValidateProgressAsync(profile, token);

            LocalSetupAccount account = await accounts.ReadAsync(token)
                ?? throw new InvalidDataException("SetupAccountMissing");
            if (account.CreatedForInstallIdentity != lease.InstallIdentity)
            {
                throw new InvalidDataException("SetupIdentityMismatch");
            }

            secrets.Reconcile(profile.AttemptId ?? Guid.Empty, profile.KeyReference);
            LocalInstallationProfile resumed = profile with
            {
                ProfileRevision = profile.ProfileRevision + 1, SetupEpoch = profile.SetupEpoch + 1,
                RuntimeGeneration = profile.RuntimeGeneration + 1, BootId = lease.BootId,
                LeaseUntilUtc = time.GetUtcNow().AddMinutes(2)
            };
            await work.ExecuteInTransactionAsync(async cancellation =>
                await work.Profiles.ReplaceAsync(resumed, profile.ProfileRevision, cancellation), token);
            state.Publish(resumed);
            logger.LogInformation("{EventName} {InstallIdentity} {AttemptId} {Phase} {Revision} {Generation}",
                "RuntimeTransitionResumed", resumed.InstallIdentity, resumed.AttemptId,
                resumed.Phase, resumed.ProfileRevision, resumed.RuntimeGeneration);
            return;
        }

        if (await accounts.ReadAsync(token) is not null || await accounts.HasProgressAsync(token))
        {
            throw new InvalidDataException("OrphanLocalSetupState");
        }

        ValidateInitialCredentials(credentials.Value);
        LocalSetupAccount created = new()
        {
            Id = Guid.NewGuid(), CreatedForInstallIdentity = lease.InstallIdentity,
            NormalizedLogin = credentials.Value.Login.Trim().ToUpperInvariant(), Stamp = Guid.NewGuid().ToString("N"), Revision = 1
        };
        PasswordHasher<LocalSetupAccount> hasher = new();
        created.PasswordHash = hasher.HashPassword(created, credentials.Value.Password);
        LocalInstallationProfile initial = new(lease.InstallIdentity, "Setup", InstallationPhase.SetupReady,
            1, 1, 1, lease.BootId, time.GetUtcNow().AddMinutes(2));
        await work.ExecuteInTransactionAsync(_ =>
        {
            accounts.Create(created);
            work.Profiles.Create(initial);
            return Task.CompletedTask;
        }, token);
        state.Publish(initial);
        logger.LogInformation("{EventName} {InstallIdentity} {Revision}", "SetupPrepared", initial.InstallIdentity, initial.ProfileRevision);
        logger.LogInformation("{EventName} {InstallIdentity}", "SetupPreflightCompleted", initial.InstallIdentity);
    }

    public static void ValidateInitialCredentials(BootstrapAdminOptions options)
    {
        string password = options.Password;
        bool placeholder = password.Contains("CHANGE", StringComparison.OrdinalIgnoreCase)
            || password.Contains("EXAMPLE", StringComparison.OrdinalIgnoreCase)
            || password.Contains("REPLACE", StringComparison.OrdinalIgnoreCase)
            || password.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(options.Login) || options.Login.Length > 256
            || string.IsNullOrWhiteSpace(password) || password.Length < 12 || placeholder)
        {
            throw new InvalidDataException("ProtectedLocalAdministratorCredentialsRequired");
        }
    }
}
