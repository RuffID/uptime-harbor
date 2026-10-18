using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Application.Users.Models;
using UptimeHarbor.Domain.Access;
using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Application.Users;

public class UserManagementService(
    IUserAccessContext accessContext,
    IAdministratorAccessPolicy administratorAccessPolicy,
    IUserAccountStore accountStore,
    IVisibilitySettingsUnitOfWork unitOfWork)
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
            IReadOnlyList<ServerVisibilityPermission> serverPermissions =
                await unitOfWork.VisibilityPermissions.GetServerPermissionsAsync(account.Id, cancellationToken);
            IReadOnlyList<ContainerVisibilityPermission> containerPermissions =
                await unitOfWork.VisibilityPermissions.GetContainerPermissionsAsync(account.Id, cancellationToken);
            users.Add(new UserAccountDto(
                account.Id,
                account.Login,
                account.IsEnabled,
                account.IsAdministrator,
                serverPermissions.Select(permission => permission.ServerId).ToList(),
                containerPermissions.Select(permission => permission.ContainerId).ToList()));
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
            new UserAccountDto(userId, login.Trim(), true, false, [], []));
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

    public async Task<ServiceResult> SetVisibilityAsync(
        Guid userId,
        IReadOnlyCollection<Guid> serverIds,
        IReadOnlyCollection<Guid> containerIds,
        CancellationToken cancellationToken = default)
    {
        ServiceError? accessError = administratorAccessPolicy.GetAccessError();
        if (accessError is not null)
        {
            return ServiceResult.Fail(accessError);
        }

        ArgumentNullException.ThrowIfNull(serverIds);
        ArgumentNullException.ThrowIfNull(containerIds);

        UserAccountRecord? account = await accountStore.GetByIdAsync(userId, cancellationToken);
        if (account is null)
        {
            return ServiceResult.Fail(ServiceErrorType.NotFound, "Пользователь не найден.");
        }

        HashSet<Guid> requestedServerIds = serverIds.ToHashSet();
        HashSet<Guid> requestedContainerIds = containerIds.ToHashSet();
        IReadOnlyList<MonitoredServer> servers =
            await unitOfWork.MonitoredServers.GetAllAsync(cancellationToken);
        if (!requestedServerIds.IsSubsetOf(servers.Select(server => server.Id)))
        {
            return ServiceResult.Fail(ServiceErrorType.Validation, "Указан несуществующий сервер.");
        }

        IReadOnlyList<MonitoredContainer> containers =
            await unitOfWork.MonitoredContainers.GetAllAsync(cancellationToken);
        List<MonitoredContainer> requestedContainers = containers
            .Where(container => requestedContainerIds.Contains(container.Id))
            .ToList();
        if (requestedContainers.Count != requestedContainerIds.Count)
        {
            return ServiceResult.Fail(ServiceErrorType.Validation, "Указан несуществующий контейнер.");
        }

        if (requestedContainers.Any(container => container.ServerId is null))
        {
            return ServiceResult.Fail(ServiceErrorType.Validation,
                "Права на контейнер без сервера доступны только администратору.");
        }

        if (requestedContainers.Any(container => !requestedServerIds.Contains(container.ServerId!.Value)))
        {
            return ServiceResult.Fail(
                ServiceErrorType.Validation,
                "Контейнер нельзя назначить без его родительского сервера.");
        }

        await unitOfWork.ExecuteInTransactionAsync(
            async transactionCancellationToken =>
            {
                IReadOnlyList<ServerVisibilityPermission> currentServerPermissions =
                    await unitOfWork.VisibilityPermissions.GetServerPermissionsAsync(
                        userId,
                        transactionCancellationToken);
                IReadOnlyList<ContainerVisibilityPermission> currentContainerPermissions =
                    await unitOfWork.VisibilityPermissions.GetContainerPermissionsAsync(
                        userId,
                        transactionCancellationToken);
                HashSet<Guid> currentServerIds = currentServerPermissions.Select(item => item.ServerId).ToHashSet();
                HashSet<Guid> currentContainerIds = currentContainerPermissions.Select(item => item.ContainerId).ToHashSet();

                unitOfWork.VisibilityPermissions.DeleteRange(
                    currentContainerPermissions.Where(item => !requestedContainerIds.Contains(item.ContainerId)));
                unitOfWork.VisibilityPermissions.DeleteRange(
                    currentServerPermissions.Where(item => !requestedServerIds.Contains(item.ServerId)));
                unitOfWork.VisibilityPermissions.CreateRange(
                    requestedServerIds
                        .Where(serverId => !currentServerIds.Contains(serverId))
                        .Select(serverId => new ServerVisibilityPermission(userId, serverId)));
                unitOfWork.VisibilityPermissions.CreateRange(
                    requestedContainers
                        .Where(container => !currentContainerIds.Contains(container.Id))
                        .Select(container => new ContainerVisibilityPermission(userId, container.ServerId!.Value, container.Id)));
            },
            cancellationToken);

        return ServiceResult.Ok();
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
