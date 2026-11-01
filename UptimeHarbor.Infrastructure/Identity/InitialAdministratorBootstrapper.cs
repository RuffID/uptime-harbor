using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Application.Users.Models;

namespace UptimeHarbor.Infrastructure.Identity;

public class InitialAdministratorBootstrapper(
    IInitialAdministratorStore store,
    IOptions<BootstrapAdminOptions> options)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureRoleAsync(ApplicationRoles.ADMINISTRATOR, cancellationToken);

        IReadOnlyList<UserAccountRecord> administrators =
            await store.GetAdministratorsAsync(cancellationToken);
        if (administrators.Any(administrator => administrator.IsEnabled))
        {
            return;
        }

        UserAccountRecord? disabledAdministrator = administrators.FirstOrDefault();
        if (disabledAdministrator is not null)
        {
            AccountStoreResult enableResult = await store.EnableAdministratorAsync(
                disabledAdministrator.Id,
                cancellationToken);
            EnsureSucceeded(enableResult, "Не удалось включить существующего администратора.");
            return;
        }

        BootstrapAdminOptions bootstrapOptions = options.Value;
        AccountStoreResult createResult = await store.CreateAdministratorAsync(
            Guid.NewGuid(),
            bootstrapOptions.Login,
            bootstrapOptions.Password,
            cancellationToken);
        EnsureSucceeded(createResult, "Не удалось создать начального администратора.");
    }

    private async Task EnsureRoleAsync(string roleName, CancellationToken cancellationToken)
    {
        AccountStoreResult result = await store.EnsureRoleAsync(roleName, cancellationToken);
        EnsureSucceeded(result, $"Не удалось подготовить роль {roleName}.");
    }

    private static void EnsureSucceeded(AccountStoreResult result, string message)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"{message} {string.Join(" ", result.Errors)}");
        }
    }
}
