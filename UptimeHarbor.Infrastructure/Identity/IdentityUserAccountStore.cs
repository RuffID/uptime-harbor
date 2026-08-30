using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Application.Users;
using UptimeHarbor.Application.Users.Models;
using UptimeHarbor.Infrastructure.Persistence;

namespace UptimeHarbor.Infrastructure.Identity;

public class IdentityUserAccountStore(
    UserManager<ApplicationUser> userManager,
    RoleManager<ApplicationRole> roleManager,
    UptimeHarborDbContext dbContext)
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

    public async Task<bool> HasOtherEnabledAdministratorAsync(
        Guid excludedUserId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await EnabledAdministrators()
            .AnyAsync(user => user.Id != excludedUserId, cancellationToken);
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
            return AccountStoreResult.Failure(
                AccountStoreFailureKind.NotFound,
                ["Пользователь не найден."]);
        }

        bool isAdministrator = await userManager.IsInRoleAsync(
            user,
            ApplicationRoles.ADMINISTRATOR);
        if (!isEnabled && isAdministrator)
        {
            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);
            ApplicationRole administratorRole = await GetAdministratorRoleAsync();
            if (await EnabledAdministrators().CountAsync(cancellationToken) <= 1)
            {
                return AccountStoreResult.Failure(
                    AccountStoreFailureKind.Conflict,
                    ["Нельзя заблокировать последнего включённого администратора."]);
            }

            user.Disable();
            IdentityResult guardedUpdateResult = await userManager.UpdateSecurityStampAsync(user);
            if (!guardedUpdateResult.Succeeded)
            {
                return Failure(guardedUpdateResult);
            }

            IdentityResult roleUpdateResult = await roleManager.UpdateAsync(administratorRole);
            if (!roleUpdateResult.Succeeded)
            {
                return Failure(roleUpdateResult);
            }

            await transaction.CommitAsync(cancellationToken);
            return AccountStoreResult.Success();
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
            return AccountStoreResult.Failure(
                AccountStoreFailureKind.NotFound,
                ["Пользователь не найден."]);
        }

        bool isAdministrator = await userManager.IsInRoleAsync(
            user,
            ApplicationRoles.ADMINISTRATOR);
        if (isAdministrator)
        {
            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);
            ApplicationRole administratorRole = await GetAdministratorRoleAsync();
            if (user.IsEnabled
                && await EnabledAdministrators().CountAsync(cancellationToken) <= 1)
            {
                return AccountStoreResult.Failure(
                    AccountStoreFailureKind.Conflict,
                    ["Нельзя удалить последнего включённого администратора."]);
            }

            IdentityResult deleteResult = await userManager.DeleteAsync(user);
            if (!deleteResult.Succeeded)
            {
                return Failure(deleteResult);
            }

            IdentityResult roleUpdateResult = await roleManager.UpdateAsync(administratorRole);
            if (!roleUpdateResult.Succeeded)
            {
                return Failure(roleUpdateResult);
            }

            await transaction.CommitAsync(cancellationToken);
            return AccountStoreResult.Success();
        }

        IdentityResult result = await userManager.DeleteAsync(user);
        return result.Succeeded ? AccountStoreResult.Success() : Failure(result);
    }

    public async Task<IReadOnlyList<UserAccountRecord>> GetAdministratorsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await roleManager.RoleExistsAsync(ApplicationRoles.ADMINISTRATOR))
        {
            return [];
        }

        IList<ApplicationUser> administrators =
            await userManager.GetUsersInRoleAsync(ApplicationRoles.ADMINISTRATOR);
        return administrators
            .OrderBy(administrator => administrator.UserName)
            .Select(administrator => Map(administrator, isAdministrator: true))
            .ToList();
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

    public Task<AccountStoreResult> EnableAdministratorAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return SetEnabledAsync(userId, true, cancellationToken);
    }

    private static UserAccountRecord Map(ApplicationUser user, bool isAdministrator)
    {
        return new UserAccountRecord(
            user.Id,
            user.UserName ?? throw new InvalidOperationException("Identity-пользователь должен иметь логин."),
            user.IsEnabled,
            isAdministrator);
    }

    private IQueryable<ApplicationUser> EnabledAdministrators()
    {
        return from user in dbContext.Users
               join userRole in dbContext.UserRoles on user.Id equals userRole.UserId
               join role in dbContext.Roles on userRole.RoleId equals role.Id
               where role.NormalizedName == ApplicationRoles.ADMINISTRATOR.ToUpperInvariant()
                   && user.IsEnabled
               select user;
    }

    private async Task<ApplicationRole> GetAdministratorRoleAsync()
    {
        ApplicationRole? role = await roleManager.FindByNameAsync(ApplicationRoles.ADMINISTRATOR);
        if (role is null)
        {
            throw new InvalidOperationException("Роль Administrator не найдена.");
        }

        return role;
    }

    private static AccountStoreResult Failure(IdentityResult result)
    {
        AccountStoreFailureKind failureKind = result.Errors.Any(
            error => string.Equals(error.Code, "ConcurrencyFailure", StringComparison.Ordinal))
            ? AccountStoreFailureKind.Conflict
            : AccountStoreFailureKind.Validation;
        return AccountStoreResult.Failure(
            failureKind,
            result.Errors.Select(error => error.Description));
    }

    private static string JoinErrors(IdentityResult result) =>
        string.Join(" ", result.Errors.Select(error => error.Description));
}
