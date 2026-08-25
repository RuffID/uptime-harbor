using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Application.Users.Models;
using UptimeHarbor.Domain.Access;
using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Application.Users;

public class UserManagementService(
    IUserAccessContext accessContext,
    IUserAccountStore accountStore,
    IVisibilitySettingsUnitOfWork unitOfWork)
    : IUserManagementService
{
    public async Task<ServiceResult<IReadOnlyList<UserAccountDto>>> GetUsersAsync(
        CancellationToken cancellationToken = default)
    {
        ServiceResult? authorizationFailure = AuthorizeAdministrator();
        if (authorizationFailure is not null)
        {
            return ServiceResult<IReadOnlyList<UserAccountDto>>.Fail(
                authorizationFailure.Error
                ?? throw new InvalidOperationException("Неуспешный результат должен содержать ошибку."));
        }

        IReadOnlyList<UserAccountRecord> accounts = await accountStore.GetAllAsync(cancellationToken);
        List<UserAccountDto> users = [];
        foreach (UserAccountRecord account in accounts)
        {
            List<ServerVisibilityPermission> serverPermissions =
                await unitOfWork.VisibilityPermissions.GetServerPermissionsAsync(account.Id, cancellationToken);
            List<ContainerVisibilityPermission> containerPermissions =
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
        ServiceResult? authorizationFailure = AuthorizeAdministrator();
        if (authorizationFailure is not null)
        {
            return ServiceResult<UserAccountDto>.Fail(
                authorizationFailure.Error
                ?? throw new InvalidOperationException("Неуспешный результат должен содержать ошибку."));
        }

        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(initialPassword))
        {
            return ServiceResult<UserAccountDto>.Fail(
                ServiceErrorType.Validation,
                "Логин и начальный пароль обязательны.");
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
        ServiceResult? authorizationFailure = AuthorizeAdministrator();
        if (authorizationFailure is not null)
        {
            return authorizationFailure;
        }

        AccountStoreResult storeResult = await accountStore.SetEnabledAsync(userId, isEnabled, cancellationToken);
        return MapStoreResult(storeResult);
    }

    public async Task<ServiceResult> DeleteAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        ServiceResult? authorizationFailure = AuthorizeAdministrator();
        if (authorizationFailure is not null)
        {
            return authorizationFailure;
        }

        UserAccountRecord? account = await accountStore.GetByIdAsync(userId, cancellationToken);
        if (account is null)
        {
            return ServiceResult.Fail(ServiceErrorType.NotFound, "Пользователь не найден.");
        }

        AccountStoreResult? deleteResult = null;
        await unitOfWork.ExecuteInTransactionAsync(
            async transactionCancellationToken =>
            {
                List<ContainerVisibilityPermission> containerPermissions =
                    await unitOfWork.VisibilityPermissions.GetContainerPermissionsAsync(
                        userId,
                        transactionCancellationToken);
                List<ServerVisibilityPermission> serverPermissions =
                    await unitOfWork.VisibilityPermissions.GetServerPermissionsAsync(
                        userId,
                        transactionCancellationToken);
                unitOfWork.VisibilityPermissions.DeleteRange(containerPermissions);
                unitOfWork.VisibilityPermissions.DeleteRange(serverPermissions);
                deleteResult = await accountStore.DeleteAsync(userId, transactionCancellationToken);
                if (!deleteResult.Succeeded)
                {
                    throw new InvalidOperationException(string.Join(" ", deleteResult.Errors));
                }
            },
            cancellationToken);

        return deleteResult is null
            ? throw new InvalidOperationException("Удаление пользователя не было выполнено.")
            : ServiceResult.Ok();
    }

    public async Task<ServiceResult> SetVisibilityAsync(
        Guid userId,
        IReadOnlyCollection<Guid> serverIds,
        IReadOnlyCollection<Guid> containerIds,
        CancellationToken cancellationToken = default)
    {
        ServiceResult? authorizationFailure = AuthorizeAdministrator();
        if (authorizationFailure is not null)
        {
            return authorizationFailure;
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
        List<MonitoredServer> servers = await unitOfWork.MonitoredServers.GetAllAsync(cancellationToken);
        if (!requestedServerIds.IsSubsetOf(servers.Select(server => server.Id)))
        {
            return ServiceResult.Fail(ServiceErrorType.Validation, "Указан несуществующий сервер.");
        }

        List<MonitoredContainer> containers = await unitOfWork.MonitoredContainers.GetAllAsync(cancellationToken);
        List<MonitoredContainer> requestedContainers = containers
            .Where(container => requestedContainerIds.Contains(container.Id))
            .ToList();
        if (requestedContainers.Count != requestedContainerIds.Count)
        {
            return ServiceResult.Fail(ServiceErrorType.Validation, "Указан несуществующий контейнер.");
        }

        if (requestedContainers.Any(container => !requestedServerIds.Contains(container.ServerId)))
        {
            return ServiceResult.Fail(
                ServiceErrorType.Validation,
                "Контейнер нельзя назначить без его родительского сервера.");
        }

        await unitOfWork.ExecuteInTransactionAsync(
            async transactionCancellationToken =>
            {
                List<ServerVisibilityPermission> currentServerPermissions =
                    await unitOfWork.VisibilityPermissions.GetServerPermissionsAsync(
                        userId,
                        transactionCancellationToken);
                List<ContainerVisibilityPermission> currentContainerPermissions =
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
                        .Select(container => new ContainerVisibilityPermission(userId, container.ServerId, container.Id)));
            },
            cancellationToken);

        return ServiceResult.Ok();
    }

    private ServiceResult? AuthorizeAdministrator()
    {
        if (!accessContext.IsAuthenticated)
        {
            return ServiceResult.Fail(ServiceErrorType.Unauthorized, "Требуется аутентификация.");
        }

        if (!accessContext.IsEnabled || !accessContext.IsAdministrator)
        {
            return ServiceResult.Fail(ServiceErrorType.Forbidden, "Операция доступна только администратору.");
        }

        return null;
    }

    private static ServiceResult MapStoreResult(AccountStoreResult storeResult)
    {
        return storeResult.Succeeded
            ? ServiceResult.Ok()
            : ServiceResult.Fail(ServiceErrorType.Validation, string.Join(" ", storeResult.Errors));
    }
}
