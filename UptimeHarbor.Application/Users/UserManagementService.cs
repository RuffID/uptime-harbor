using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Application.Users.Models;

namespace UptimeHarbor.Application.Users;

public class UserManagementService(
    IUserAccessContext accessContext,
    IAdministratorAccessPolicy administratorAccessPolicy,
    IUserAccountStore accountStore)
    : IUserManagementService
{
    public async Task<ServiceResult<IReadOnlyList<UserAccountDto>>> GetUsersAsync(
        CancellationToken cancellationToken = default)
    {
        ServiceError? accessError = administratorAccessPolicy.GetAccessError();
        if (accessError is not null)
        {
            return ServiceResult<IReadOnlyList<UserAccountDto>>.Fail(accessError);
        }

        IReadOnlyList<UserAccountRecord> accounts = await accountStore.GetAllAsync(cancellationToken);
        List<UserAccountDto> users = [];
        foreach (UserAccountRecord account in accounts)
        {
            users.Add(new UserAccountDto(
                account.Id,
                account.Login,
                account.IsEnabled,
                account.IsAdministrator,
                [], []));
        }

        return ServiceResult<IReadOnlyList<UserAccountDto>>.Ok(users);
    }

    public async Task<ServiceResult<UserAccountDto>> CreateUserAsync(
        string login,
        string initialPassword,
        CancellationToken cancellationToken = default)
    {
        ServiceError? accessError = administratorAccessPolicy.GetAccessError();
        if (accessError is not null)
        {
            return ServiceResult<UserAccountDto>.Fail(accessError);
        }

        if (string.IsNullOrWhiteSpace(login))
        {
            return ServiceResult<UserAccountDto>.Fail(
                ServiceErrorType.Validation,
                "Логин обязателен.",
                "login");
        }

        if (string.IsNullOrWhiteSpace(initialPassword))
        {
            return ServiceResult<UserAccountDto>.Fail(
                ServiceErrorType.Validation,
                "Начальный пароль обязателен.",
                "initialPassword");
        }

        Guid userId = Guid.NewGuid();
        AccountStoreResult storeResult = await accountStore.CreateAsync(
            userId,
            login.Trim(),
            initialPassword,
            cancellationToken);
        if (!storeResult.Succeeded)
        {
            return ServiceResult<UserAccountDto>.Fail(
                ServiceErrorType.Validation,
                string.Join(" ", storeResult.Errors));
        }

        return ServiceResult<UserAccountDto>.Ok(
            new UserAccountDto(userId, login.Trim(), true, true, [], []));
    }

    public async Task<ServiceResult> SetEnabledAsync(
        Guid userId,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        ServiceError? accessError = administratorAccessPolicy.GetAccessError();
        if (accessError is not null)
        {
            return ServiceResult.Fail(accessError);
        }

        if (userId == accessContext.UserId)
        {
            return ServiceResult.Fail(
                ServiceErrorType.Forbidden,
                "Нельзя изменить состояние собственной учётной записи.");
        }

        UserAccountRecord? account = await accountStore.GetByIdAsync(userId, cancellationToken);
        if (account is null)
        {
            return ServiceResult.Fail(ServiceErrorType.NotFound, "Пользователь не найден.");
        }

        if (!isEnabled
            && account.IsAdministrator
            && account.IsEnabled
            && !await accountStore.HasOtherEnabledAdministratorAsync(userId, cancellationToken))
        {
            return ServiceResult.Fail(
                ServiceErrorType.Conflict,
                "Нельзя заблокировать последнего включённого администратора.");
        }

        AccountStoreResult storeResult = await accountStore.SetEnabledAsync(userId, isEnabled, cancellationToken);
        return MapStoreResult(storeResult);
    }

    public async Task<ServiceResult> DeleteAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        ServiceError? accessError = administratorAccessPolicy.GetAccessError();
        if (accessError is not null)
        {
            return ServiceResult.Fail(accessError);
        }

        if (userId == accessContext.UserId)
        {
            return ServiceResult.Fail(
                ServiceErrorType.Forbidden,
                "Нельзя удалить собственную учётную запись.");
        }

        UserAccountRecord? account = await accountStore.GetByIdAsync(userId, cancellationToken);
        if (account is null)
        {
            return ServiceResult.Fail(ServiceErrorType.NotFound, "Пользователь не найден.");
        }

        if (account.IsAdministrator
            && account.IsEnabled
            && !await accountStore.HasOtherEnabledAdministratorAsync(userId, cancellationToken))
        {
            return ServiceResult.Fail(
                ServiceErrorType.Conflict,
                "Нельзя удалить последнего включённого администратора.");
        }

        AccountStoreResult deleteResult = await accountStore.DeleteAsync(userId, cancellationToken);
        return MapStoreResult(deleteResult);
    }

    private static ServiceResult MapStoreResult(AccountStoreResult storeResult)
    {
        if (storeResult.Succeeded)
        {
            return ServiceResult.Ok();
        }

        ServiceErrorType errorType = storeResult.FailureKind switch
        {
            AccountStoreFailureKind.Validation => ServiceErrorType.Validation,
            AccountStoreFailureKind.NotFound => ServiceErrorType.NotFound,
            AccountStoreFailureKind.Conflict => ServiceErrorType.Conflict,
            null => throw new InvalidOperationException("Ошибка account store должна содержать тип."),
            _ => throw new ArgumentOutOfRangeException(
                nameof(storeResult.FailureKind),
                storeResult.FailureKind,
                "Неизвестный тип ошибки account store.")
        };
        return ServiceResult.Fail(errorType, string.Join(" ", storeResult.Errors));
    }
}
