using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.ObjectAdministration.Models;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain;
using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Application.ObjectAdministration;

public class ObjectAdministrationService(
    IAdministratorAccessPolicy administratorAccessPolicy,
    IObjectAdministrationUnitOfWork unitOfWork,
    IObjectAdministrationChangePublisher changePublisher)
    : IObjectAdministrationService
{
    public async Task<ServiceResult<IReadOnlyList<AdminServerDto>>> GetServersAsync(
        CancellationToken cancellationToken = default)
    {
        ServiceError? accessError = administratorAccessPolicy.GetAccessError();
        if (accessError is not null)
        {
            return ServiceResult<IReadOnlyList<AdminServerDto>>.Fail(accessError);
        }

        IReadOnlyList<MonitoredServer> servers =
            await unitOfWork.MonitoredServers.GetAllAsync(cancellationToken);
        return ServiceResult<IReadOnlyList<AdminServerDto>>.Ok(servers.Select(MapServer).ToList());
    }

    public async Task<ServiceResult<IReadOnlyList<AdminContainerDto>>> GetContainersAsync(
        Guid? serverId = null,
        CancellationToken cancellationToken = default)
    {
        ServiceError? accessError = administratorAccessPolicy.GetAccessError();
        if (accessError is not null)
        {
            return ServiceResult<IReadOnlyList<AdminContainerDto>>.Fail(accessError);
        }

        if (serverId is null)
        {
            IReadOnlyList<MonitoredServer> allContainers =
                await unitOfWork.MonitoredContainers.GetAllAsync(cancellationToken);
            return ServiceResult<IReadOnlyList<AdminContainerDto>>.Ok(allContainers.Select(MapContainer).ToList());
        }

        MonitoredServer? server = await unitOfWork.MonitoredServers.GetItemByIdAsync(serverId.Value, cancellationToken);
        if (server is null)
        {
            return ServiceResult<IReadOnlyList<AdminContainerDto>>.Fail(
                ServiceErrorType.NotFound,
                "Сервер не найден.");
        }

        IReadOnlyList<MonitoredServer> containers =
            await unitOfWork.MonitoredContainers.GetByServerIdAsync(serverId.Value, cancellationToken);
        return ServiceResult<IReadOnlyList<AdminContainerDto>>.Ok(
            containers.Select(MapContainer).ToList());
    }

    public async Task<ServiceResult<AdminServerDto>> CreateServerAsync(
        string name,
        string endpointUrl,
        string? description,
        bool isMonitoringEnabled,
        CancellationToken cancellationToken = default)
    {
        ServiceError? error = administratorAccessPolicy.GetAccessError()
            ?? ValidateServerSettings(name, endpointUrl, description);
        if (error is not null)
        {
            return ServiceResult<AdminServerDto>.Fail(error);
        }

        string normalizedName = name.Trim();
        IReadOnlyList<MonitoredServer> servers =
            await unitOfWork.MonitoredServers.GetAllAsync(cancellationToken);
        if (servers.Any(server => string.Equals(
            server.Name,
            normalizedName,
            StringComparison.OrdinalIgnoreCase)))
        {
            return ServiceResult<AdminServerDto>.Fail(CreateDuplicateServerNameError());
        }

        MonitoredServer server = new(
            Guid.NewGuid(),
            normalizedName,
            endpointUrl,
            description,
            isMonitoringEnabled);
        unitOfWork.MonitoredServers.Create(server);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (PersistenceConflictException exception)
            when (exception.Kind == PersistenceConflictKind.DuplicateServerName)
        {
            return ServiceResult<AdminServerDto>.Fail(CreateDuplicateServerNameError());
        }
        await changePublisher.PublishObjectsChangedAsync(cancellationToken);

        return ServiceResult<AdminServerDto>.Ok(MapServer(server));
    }

    public async Task<ServiceResult<AdminServerDto>> UpdateServerAsync(
        Guid serverId,
        string name,
        string endpointUrl,
        string? description,
        bool isMonitoringEnabled,
        long expectedVersion,
        CancellationToken cancellationToken = default,
        IReadOnlyList<ContainerLinkUpdate>? containerLinks = null)
    {
        ServiceError? error = administratorAccessPolicy.GetAccessError()
            ?? ValidateServerSettings(name, endpointUrl, description);
        if (error is not null)
        {
            return ServiceResult<AdminServerDto>.Fail(error);
        }

        MonitoredServer? server = await unitOfWork.MonitoredServers.GetItemByIdAsync(serverId, cancellationToken);
        if (server is null)
        {
            return ServiceResult<AdminServerDto>.Fail(ServiceErrorType.NotFound, "Сервер не найден.");
        }

        if (server.Version != expectedVersion)
        {
            return ServiceResult<AdminServerDto>.Fail(CreateConcurrencyError());
        }

        string normalizedName = name.Trim();
        IReadOnlyList<MonitoredServer> servers =
            await unitOfWork.MonitoredServers.GetAllAsync(cancellationToken);
        if (servers.Any(candidate => candidate.Id != server.Id && string.Equals(
            candidate.Name,
            normalizedName,
            StringComparison.OrdinalIgnoreCase)))
        {
            return ServiceResult<AdminServerDto>.Fail(CreateDuplicateServerNameError());
        }

        IReadOnlyList<MonitoredServer> containers = [];
        if (containerLinks is not null)
        {
            containers = await unitOfWork.MonitoredContainers.GetAllAsync(cancellationToken);
            ServiceError? linksError = ValidateContainerLinks(serverId, containerLinks, containers);
            if (linksError is not null)
            {
                return ServiceResult<AdminServerDto>.Fail(linksError);
            }
        }

        try
        {
            await unitOfWork.ExecuteInTransactionAsync(
                async transactionCancellationToken =>
                {
                    if (containerLinks is not null)
                    {
                        foreach (ContainerLinkUpdate link in containerLinks)
                        {
                            MonitoredServer container = containers.Single(item => item.Id == link.Id);
                            Guid? parentId = null;
                            if (link.IsLinked)
                            {
                                parentId = serverId;
                            }

                            await AssignContainerServerAsync(container, parentId, transactionCancellationToken);
                        }
                    }

                    Guid? incidentId = server.CurrentIncidentId;
                    RecoveryNotificationCancellationReason? cancellationReason =
                        GetRecoveryCancellationReason(
                            server.EndpointUrl,
                            endpointUrl,
                            server.IsMonitoringEnabled,
                            isMonitoringEnabled);
                    server.UpdateSettings(normalizedName, endpointUrl, description, isMonitoringEnabled);
                    if (incidentId is Guid requiredIncidentId
                        && cancellationReason is RecoveryNotificationCancellationReason requiredReason)
                    {
                        await unitOfWork.CancelPendingRecoveryNotificationsAsync(
                            MonitoringObjectKind.Server,
                            server.Id,
                            requiredIncidentId,
                            requiredReason,
                            transactionCancellationToken);
                    }
                },
                cancellationToken);
        }
        catch (PersistenceConcurrencyException)
        {
            return ServiceResult<AdminServerDto>.Fail(CreateConcurrencyError());
        }
        catch (PersistenceConflictException exception)
            when (exception.Kind == PersistenceConflictKind.DuplicateServerName)
        {
            return ServiceResult<AdminServerDto>.Fail(CreateDuplicateServerNameError());
        }
        await changePublisher.PublishObjectsChangedAsync(cancellationToken);
        return ServiceResult<AdminServerDto>.Ok(MapServer(server));
    }

    public async Task<ServiceResult<AdminContainerDto>> CreateContainerAsync(
        Guid? serverId,
        string endpointUrl,
        string? description,
        bool isMonitoringEnabled,
        CancellationToken cancellationToken = default,
        string? name = null)
    {
        ServiceError? error = administratorAccessPolicy.GetAccessError()
            ?? ValidateApplicationSettings(name, endpointUrl, description);
        if (error is not null)
        {
            return ServiceResult<AdminContainerDto>.Fail(error);
        }

        ServiceError? serverError = await ValidateServerAsync(serverId, cancellationToken);
        if (serverError is not null)
        {
            return ServiceResult<AdminContainerDto>.Fail(serverError);
        }

        MonitoredServer container = MonitoredServer.CreateApplication(
            Guid.NewGuid(),
            serverId,
            endpointUrl,
            description,
            isMonitoringEnabled,
            name);

        try
        {
            await unitOfWork.ExecuteInTransactionAsync(async transactionCancellationToken =>
            {
                unitOfWork.MonitoredContainers.Create(container);
                await TouchServerAsync(serverId, transactionCancellationToken);
            }, cancellationToken);
        }
        catch (PersistenceConcurrencyException)
        {
            return ServiceResult<AdminContainerDto>.Fail(CreateConcurrencyError());
        }

        await changePublisher.PublishObjectsChangedAsync(cancellationToken);

        return ServiceResult<AdminContainerDto>.Ok(MapContainer(container));
    }

    public async Task<ServiceResult<AdminContainerDto>> UpdateContainerAsync(
        Guid containerId,
        string endpointUrl,
        string? description,
        bool isMonitoringEnabled,
        long expectedVersion,
        CancellationToken cancellationToken = default,
        ContainerServerAssignment? serverAssignment = null,
        string? name = null)
    {
        ServiceError? error = administratorAccessPolicy.GetAccessError()
            ?? ValidateApplicationSettings(name, endpointUrl, description);
        if (error is not null)
        {
            return ServiceResult<AdminContainerDto>.Fail(error);
        }

        MonitoredServer? container =
            await unitOfWork.MonitoredContainers.GetItemByIdAsync(containerId, cancellationToken);
        if (container is null)
        {
            return ServiceResult<AdminContainerDto>.Fail(ServiceErrorType.NotFound, "Приложение не найдено.");
        }

        if (container.Version != expectedVersion)
        {
            return ServiceResult<AdminContainerDto>.Fail(CreateConcurrencyError());
        }

        Guid? parentId = container.ServerId;
        if (serverAssignment is not null)
        {
            parentId = serverAssignment.ServerId;
        }

        ServiceError? serverError = await ValidateServerAsync(parentId, cancellationToken);
        if (serverError is not null)
        {
            return ServiceResult<AdminContainerDto>.Fail(serverError);
        }

        try
        {
            await unitOfWork.ExecuteInTransactionAsync(
                async transactionCancellationToken =>
                {
                    await AssignContainerServerAsync(container, parentId, transactionCancellationToken);
                    Guid? incidentId = container.CurrentIncidentId;
                    RecoveryNotificationCancellationReason? cancellationReason =
                        GetRecoveryCancellationReason(
                            container.EndpointUrl,
                            endpointUrl,
                            container.IsMonitoringEnabled,
                            isMonitoringEnabled);
                    container.UpdateSettings(name ?? container.Name, endpointUrl, description, isMonitoringEnabled);
                    if (incidentId is Guid requiredIncidentId
                        && cancellationReason is RecoveryNotificationCancellationReason requiredReason)
                    {
                        await unitOfWork.CancelPendingRecoveryNotificationsAsync(
                            MonitoringObjectKind.Container,
                            container.Id,
                            requiredIncidentId,
                            requiredReason,
                            transactionCancellationToken);
                    }
                },
                cancellationToken);
        }
        catch (PersistenceConcurrencyException)
        {
            return ServiceResult<AdminContainerDto>.Fail(CreateConcurrencyError());
        }
        await changePublisher.PublishObjectsChangedAsync(cancellationToken);
        return ServiceResult<AdminContainerDto>.Ok(MapContainer(container));
    }

    public async Task<ServiceResult<ServerDeletionImpactDto>> GetServerDeletionImpactAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        ServiceError? accessError = administratorAccessPolicy.GetAccessError();
        if (accessError is not null)
        {
            return ServiceResult<ServerDeletionImpactDto>.Fail(accessError);
        }

        MonitoredServer? server = await unitOfWork.MonitoredServers.GetItemByIdAsync(serverId, cancellationToken);
        if (server is null)
        {
            return ServiceResult<ServerDeletionImpactDto>.Fail(ServiceErrorType.NotFound, "Сервер не найден.");
        }

        IReadOnlyList<MonitoredServer> containers =
            await unitOfWork.MonitoredContainers.GetByServerIdAsync(serverId, cancellationToken);
        return ServiceResult<ServerDeletionImpactDto>.Ok(
            new(server.Id, containers.Count, server.Version));
    }

    public async Task<ServiceResult> DeleteServerAsync(
        Guid serverId,
        int expectedContainerCount,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ServiceError? accessError = administratorAccessPolicy.GetAccessError();
        if (accessError is not null)
        {
            return ServiceResult.Fail(accessError);
        }

        if (expectedContainerCount < 0)
        {
            return ServiceResult.Fail(
                ServiceErrorType.Validation,
                "Количество приложений не может быть отрицательным.",
                "expectedContainerCount");
        }

        ServiceResult? operationResult = null;
        try
        {
            await unitOfWork.ExecuteInTransactionAsync(
                async transactionCancellationToken =>
                {
                    MonitoredServer? server = await unitOfWork.MonitoredServers.GetItemByIdAsync(
                        serverId,
                        transactionCancellationToken);
                    if (server is null)
                    {
                        operationResult = ServiceResult.Fail(ServiceErrorType.NotFound, "Сервер не найден.");
                        return;
                    }

                    if (server.Version != expectedVersion)
                    {
                        operationResult = ServiceResult.Fail(CreateConcurrencyError());
                        return;
                    }

                    IReadOnlyList<MonitoredServer> containers =
                        await unitOfWork.MonitoredContainers.GetByServerIdAsync(
                            serverId,
                            transactionCancellationToken);
                    if (containers.Count != expectedContainerCount)
                    {
                        operationResult = ServiceResult.Fail(
                            ServiceErrorType.Conflict,
                            "Количество приложений изменилось. Повторите подтверждение удаления.");
                        return;
                    }

                    foreach (MonitoredServer container in containers)
                    {
                        container.AssignServer(null);
                    }

                    await unitOfWork.RemoveNotificationBindingsAsync(serverId, transactionCancellationToken);
                    unitOfWork.MonitoredServers.Delete(server);
                    operationResult = ServiceResult.Ok();
                },
                cancellationToken);
        }
        catch (PersistenceConcurrencyException)
        {
            return ServiceResult.Fail(CreateConcurrencyError());
        }

        ServiceResult result = operationResult
            ?? throw new InvalidOperationException("Удаление сервера не было выполнено.");
        if (result.Success)
        {
            await changePublisher.PublishObjectsChangedAsync(cancellationToken);
        }

        return result;
    }

    public async Task<ServiceResult> DeleteContainerAsync(
        Guid containerId,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ServiceError? accessError = administratorAccessPolicy.GetAccessError();
        if (accessError is not null)
        {
            return ServiceResult.Fail(accessError);
        }

        MonitoredServer? container =
            await unitOfWork.MonitoredContainers.GetItemByIdAsync(containerId, cancellationToken);
        if (container is null)
        {
            return ServiceResult.Fail(ServiceErrorType.NotFound, "Приложение не найдено.");
        }

        if (container.Version != expectedVersion)
        {
            return ServiceResult.Fail(CreateConcurrencyError());
        }

        await unitOfWork.RemoveNotificationBindingsAsync(containerId, cancellationToken);
        unitOfWork.MonitoredContainers.Delete(container);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (PersistenceConcurrencyException)
        {
            return ServiceResult.Fail(CreateConcurrencyError());
        }
        await changePublisher.PublishObjectsChangedAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    private async Task<ServiceError?> ValidateServerAsync(Guid? serverId, CancellationToken cancellationToken)
    {
        if (serverId is null)
        {
            return null;
        }

        MonitoredServer? server = await unitOfWork.MonitoredServers.GetItemByIdAsync(serverId.Value, cancellationToken);
        if (server is null || server.Type != ServerType.Server)
        {
            return new ServiceError(ServiceErrorType.Validation, "Связь можно установить только с существующим сервером.", "serverId");
        }

        return null;
    }

    private async Task AssignContainerServerAsync(
        MonitoredServer container,
        Guid? serverId,
        CancellationToken cancellationToken)
    {
        if (container.ServerId == serverId)
        {
            return;
        }

        await TouchServerAsync(container.ServerId, cancellationToken);
        await TouchServerAsync(serverId, cancellationToken);
        container.AssignServer(serverId);
    }

    private async Task TouchServerAsync(Guid? serverId, CancellationToken cancellationToken)
    {
        if (serverId is null)
        {
            return;
        }

        MonitoredServer server = await unitOfWork.MonitoredServers.GetItemByIdAsync(serverId.Value, cancellationToken)
            ?? throw new InvalidOperationException("Сервер связи не найден.");
        server.RecordContainerLinksChanged();
    }

    private static ServiceError? ValidateContainerLinks(
        Guid serverId,
        IReadOnlyList<ContainerLinkUpdate> links,
        IReadOnlyList<MonitoredServer> containers)
    {
        HashSet<Guid> requestedIds = links.Select(link => link.Id).ToHashSet();
        if (requestedIds.Count != links.Count)
        {
            return new ServiceError(ServiceErrorType.Validation, "Приложения не должны повторяться.", "containerLinks");
        }

        foreach (ContainerLinkUpdate link in links)
        {
            MonitoredServer? container = containers.SingleOrDefault(item => item.Id == link.Id);
            if (container is null || container.Version != link.Version)
            {
                return CreateConcurrencyError();
            }

            if (container.ServerId is Guid parentId && parentId != serverId)
            {
                return new ServiceError(ServiceErrorType.Conflict,
                    "Приложение уже связано с другим сервером. Обновите список.", "containerLinks");
            }
        }

        if (containers.Any(container => container.ServerId == serverId && !requestedIds.Contains(container.Id)))
        {
            return CreateConcurrencyError();
        }

        return null;
    }

    private static ServiceError? ValidateSettings(string endpointUrl, string? description)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl))
        {
            return new ServiceError(ServiceErrorType.Validation, "Endpoint обязателен.", "endpointUrl");
        }

        string normalizedEndpoint = endpointUrl.Trim();
        if (normalizedEndpoint.Length > ModelConstraints.ENDPOINT_URL_MAX_LENGTH)
        {
            return new ServiceError(
                ServiceErrorType.Validation,
                $"Endpoint не должен превышать {ModelConstraints.ENDPOINT_URL_MAX_LENGTH} символов.",
                "endpointUrl");
        }

        if (!Uri.TryCreate(normalizedEndpoint, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return new ServiceError(
                ServiceErrorType.Validation,
                "Endpoint должен быть абсолютным HTTP/HTTPS URL.",
                "endpointUrl");
        }

        if (description?.Trim().Length > ModelConstraints.DESCRIPTION_MAX_LENGTH)
        {
            return new ServiceError(
                ServiceErrorType.Validation,
                $"Описание не должно превышать {ModelConstraints.DESCRIPTION_MAX_LENGTH} символов.",
                "description");
        }

        return null;
    }

    private static ServiceError? ValidateApplicationSettings(string? name, string endpointUrl, string? description)
    {
        if (name is not null)
        {
            return ValidateServerSettings(name, endpointUrl, description);
        }

        return ValidateSettings(endpointUrl, description);
    }

    private static ServiceError? ValidateServerSettings(
        string name,
        string endpointUrl,
        string? description)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new ServiceError(ServiceErrorType.Validation, "Имя сервера обязательно.", "name");
        }

        if (name.Trim().Length > ModelConstraints.OBJECT_NAME_MAX_LENGTH)
        {
            return new ServiceError(
                ServiceErrorType.Validation,
                $"Имя сервера не должно превышать {ModelConstraints.OBJECT_NAME_MAX_LENGTH} символов.",
                "name");
        }

        return ValidateSettings(endpointUrl, description);
    }

    private static AdminServerDto MapServer(MonitoredServer server)
    {
        return new AdminServerDto(
            server.Id,
            server.Name,
            server.EndpointUrl,
            server.Description,
            server.IsMonitoringEnabled,
            server.LastKnownName,
            server.Status,
            server.ConsecutiveFailures,
            server.Version);
    }

    private static AdminContainerDto MapContainer(MonitoredServer container)
    {
        return new AdminContainerDto(
            container.Id,
            container.ServerId,
            container.EndpointUrl,
            container.Description,
            container.IsMonitoringEnabled,
            container.LastKnownName,
            container.Status,
            container.ConsecutiveFailures,
            container.Version,
            container.Name);
    }

    private static ServiceError CreateConcurrencyError()
    {
        return new ServiceError(
            ServiceErrorType.Conflict,
            "Объект был изменён другим процессом. Обновите данные и повторите операцию.");
    }

    private static ServiceError CreateDuplicateServerNameError()
    {
        return new ServiceError(
            ServiceErrorType.Conflict,
            "Сервер с таким именем уже существует.",
            "name");
    }

    private static RecoveryNotificationCancellationReason? GetRecoveryCancellationReason(
        string currentEndpointUrl,
        string newEndpointUrl,
        bool isMonitoringEnabled,
        bool willMonitoringBeEnabled)
    {
        if (!string.Equals(currentEndpointUrl, newEndpointUrl.Trim(), StringComparison.Ordinal))
        {
            return RecoveryNotificationCancellationReason.EndpointChanged;
        }

        return isMonitoringEnabled && !willMonitoringBeEnabled
            ? RecoveryNotificationCancellationReason.MonitoringDisabled
            : null;
    }
}
