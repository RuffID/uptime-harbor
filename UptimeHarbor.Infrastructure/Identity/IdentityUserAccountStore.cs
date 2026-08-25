using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Application.Users;
using UptimeHarbor.Application.Users.Models;

namespace UptimeHarbor.Infrastructure.Identity;

public class IdentityUserAccountStore(
    UserManager<ApplicationUser> userManager,
    RoleManager<ApplicationRole> roleManager)
    : IUserAccountStore, IInitialAdministratorStore
{
    public async Task<IReadOnlyList<UserAccountRecord>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        List<ApplicationUser> users = await userManager.Users
            .AsNoTracking()
            .OrderBy(user => user.UserName)
            .ToListAsync(cancellationToken);
        List<UserAccountRecord> result = [];
        foreach (ApplicationUser user in users)
        {
            bool isAdministrator = await userManager.IsInRoleAsync(user, ApplicationRoles.ADMINISTRATOR);
            result.Add(Map(user, isAdministrator));
        }

        return result;
    }

    public async Task<UserAccountRecord?> GetByIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplicationUser? user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return null;
        }

        bool isAdministrator = await userManager.IsInRoleAsync(user, ApplicationRoles.ADMINISTRATOR);
        return Map(user, isAdministrator);
    }

    public async Task<AccountStoreResult> CreateAsync(
        Guid userId,
        string login,
        string initialPassword,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplicationUser user = new(userId, login);
        IdentityResult createResult = await userManager.CreateAsync(user, initialPassword);
        if (!createResult.Succeeded)
        {
            return Failure(createResult);
        }

        IdentityResult roleResult = await userManager.AddToRoleAsync(user, ApplicationRoles.USER);
        if (!roleResult.Succeeded)
        {
            IdentityResult cleanupResult = await userManager.DeleteAsync(user);
            if (!cleanupResult.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Не удалось откатить создание пользователя: {JoinErrors(cleanupResult)}");
            }

            return Failure(roleResult);
        }

        return AccountStoreResult.Success();
    }

    public async Task<AccountStoreResult> SetEnabledAsync(
        Guid userId,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplicationUser? user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AccountStoreResult.Failure(["Пользователь не найден."]);
        }

        if (isEnabled)
        {
            user.Enable();
        }
        else
        {
            user.Disable();
        }

        IdentityResult updateResult = await userManager.UpdateSecurityStampAsync(user);
        return updateResult.Succeeded ? AccountStoreResult.Success() : Failure(updateResult);
    }

    public async Task<AccountStoreResult> DeleteAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplicationUser? user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AccountStoreResult.Failure(["Пользователь не найден."]);
        }

        IdentityResult result = await userManager.DeleteAsync(user);
        return result.Succeeded ? AccountStoreResult.Success() : Failure(result);
    }

    public async Task<bool> AdministratorExistsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await roleManager.RoleExistsAsync(ApplicationRoles.ADMINISTRATOR))
        {
            return false;
        }

        IList<ApplicationUser> administrators =
            await userManager.GetUsersInRoleAsync(ApplicationRoles.ADMINISTRATOR);
        return administrators.Count > 0;
    }

    public async Task<AccountStoreResult> EnsureRoleAsync(
        string roleName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (await roleManager.RoleExistsAsync(roleName))
        {
            return AccountStoreResult.Success();
        }

        IdentityResult result = await roleManager.CreateAsync(new ApplicationRole(Guid.NewGuid(), roleName));
        return result.Succeeded ? AccountStoreResult.Success() : Failure(result);
    }

    public async Task<AccountStoreResult> CreateAdministratorAsync(
        Guid userId,
        string login,
        string password,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplicationUser user = new(userId, login);
        IdentityResult createResult = await userManager.CreateAsync(user, password);
        if (!createResult.Succeeded)
        {
            return Failure(createResult);
        }

        IdentityResult roleResult = await userManager.AddToRoleAsync(user, ApplicationRoles.ADMINISTRATOR);
        if (!roleResult.Succeeded)
        {
            IdentityResult cleanupResult = await userManager.DeleteAsync(user);
            if (!cleanupResult.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Не удалось откатить создание администратора: {JoinErrors(cleanupResult)}");
            }

            return Failure(roleResult);
        }

        return AccountStoreResult.Success();
    }

    private static UserAccountRecord Map(ApplicationUser user, bool isAdministrator)
    {
        return new UserAccountRecord(
            user.Id,
            user.UserName ?? throw new InvalidOperationException("Identity-пользователь должен иметь логин."),
            user.IsEnabled,
            isAdministrator);
    }

    private static AccountStoreResult Failure(IdentityResult result) =>
        AccountStoreResult.Failure(result.Errors.Select(error => error.Description));

    private static string JoinErrors(IdentityResult result) =>
        string.Join(" ", result.Errors.Select(error => error.Description));
}
