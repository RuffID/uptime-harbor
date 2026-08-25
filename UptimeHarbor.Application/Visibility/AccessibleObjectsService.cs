using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Application.Visibility.Models;
using UptimeHarbor.Domain.Access;
using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Application.Visibility;

public class AccessibleObjectsService(
    IUserAccessContext accessContext,
    IVisibilitySettingsUnitOfWork unitOfWork)
    : IAccessibleObjectsService
{
    public async Task<ServiceResult<IReadOnlyList<AccessibleServerDto>>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        ServiceError? accessError = GetAccessError();
        if (accessError is not null)
        {
            return ServiceResult<IReadOnlyList<AccessibleServerDto>>.Fail(accessError);
        }

        List<MonitoredServer> servers = await unitOfWork.MonitoredServers.GetAllAsync(cancellationToken);
        List<MonitoredContainer> containers = await unitOfWork.MonitoredContainers.GetAllAsync(cancellationToken);

        HashSet<Guid> serverIds = [];
        HashSet<Guid> containerIds = [];
        if (!accessContext.IsAdministrator)
        {
            List<ServerVisibilityPermission> serverPermissions =
                await unitOfWork.VisibilityPermissions.GetServerPermissionsAsync(
                    accessContext.UserId,
                    cancellationToken);
            List<ContainerVisibilityPermission> containerPermissions =
                await unitOfWork.VisibilityPermissions.GetContainerPermissionsAsync(
                    accessContext.UserId,
                    cancellationToken);
            serverIds = serverPermissions.Select(permission => permission.ServerId).ToHashSet();
            containerIds = containerPermissions.Select(permission => permission.ContainerId).ToHashSet();
        }

        List<AccessibleServerDto> result = servers
            .Where(server => ObjectVisibilityPolicy.CanAccessServer(
                accessContext.IsAdministrator,
                server.Id,
                serverIds))
            .Select(server => MapServer(
                server,
                containers.Where(container =>
                    container.ServerId == server.Id
                    && ObjectVisibilityPolicy.CanAccessContainer(
                        accessContext.IsAdministrator,
                        container.ServerId,
                        container.Id,
                        serverIds,
                        containerIds))))
            .ToList();

        return ServiceResult<IReadOnlyList<AccessibleServerDto>>.Ok(result);
    }

    public async Task<ServiceResult<AccessibleServerDto>> GetServerAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        ServiceResult<IReadOnlyList<AccessibleServerDto>> result = await GetAllAsync(cancellationToken);
        if (!result.Success)
        {
            return ServiceResult<AccessibleServerDto>.Fail(
                result.Error
                ?? throw new InvalidOperationException("Неуспешный результат должен содержать ошибку."));
        }

        AccessibleServerDto? server = result.Data!.SingleOrDefault(item => item.Id == serverId);
        return server is null
            ? ServiceResult<AccessibleServerDto>.Fail(ServiceErrorType.NotFound, "Сервер не найден.")
            : ServiceResult<AccessibleServerDto>.Ok(server);
    }

    public async Task<ServiceResult<AccessibleContainerDto>> GetContainerAsync(
        Guid containerId,
        CancellationToken cancellationToken = default)
    {
        ServiceResult<IReadOnlyList<AccessibleServerDto>> result = await GetAllAsync(cancellationToken);
        if (!result.Success)
        {
            return ServiceResult<AccessibleContainerDto>.Fail(
                result.Error
                ?? throw new InvalidOperationException("Неуспешный результат должен содержать ошибку."));
        }

        AccessibleContainerDto? container = result.Data!
            .SelectMany(server => server.Containers)
            .SingleOrDefault(item => item.Id == containerId);
        return container is null
            ? ServiceResult<AccessibleContainerDto>.Fail(ServiceErrorType.NotFound, "Контейнер не найден.")
            : ServiceResult<AccessibleContainerDto>.Ok(container);
    }

    public async Task<bool> CanAccessServerAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        ServiceResult<AccessibleServerDto> result = await GetServerAsync(serverId, cancellationToken);
        return result.Success;
    }

    public async Task<bool> CanAccessContainerAsync(Guid containerId, CancellationToken cancellationToken = default)
    {
        ServiceResult<AccessibleContainerDto> result = await GetContainerAsync(containerId, cancellationToken);
        return result.Success;
    }

    private ServiceError? GetAccessError()
    {
        if (!accessContext.IsAuthenticated)
        {
            return new ServiceError(ServiceErrorType.Unauthorized, "Требуется аутентификация.");
        }

        if (!accessContext.IsEnabled)
        {
            return new ServiceError(ServiceErrorType.Forbidden, "Пользователь заблокирован.");
        }

        return null;
    }

    private static AccessibleServerDto MapServer(
        MonitoredServer server,
        IEnumerable<MonitoredContainer> containers)
    {
        return new AccessibleServerDto(
            server.Id,
            server.EndpointUrl,
            server.Description,
            server.LastKnownName,
            server.Status,
            server.LastCheckedAtUtc,
            containers.Select(MapContainer).ToList());
    }

    private static AccessibleContainerDto MapContainer(MonitoredContainer container)
    {
        return new AccessibleContainerDto(
            container.Id,
            container.ServerId,
            container.EndpointUrl,
            container.Description,
            container.LastKnownName,
            container.Status,
            container.LastCheckedAtUtc,
            container.StartedAtUtc);
    }
}
