using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Common;
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
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        ServiceError? accessError = administratorAccessPolicy.GetAccessError();
        if (accessError is not null)
        {
            return ServiceResult<IReadOnlyList<AdminContainerDto>>.Fail(accessError);
        }

        MonitoredServer? server = await unitOfWork.MonitoredServers.GetItemByIdAsync(serverId, cancellationToken);
        if (server is null)
        {
            return ServiceResult<IReadOnlyList<AdminContainerDto>>.Fail(
                ServiceErrorType.NotFound,
                "Сервер не найден.");
        }

        IReadOnlyList<MonitoredContainer> containers =
            await unitOfWork.MonitoredContainers.GetByServerIdAsync(serverId, cancellationToken);
        return ServiceResult<IReadOnlyList<AdminContainerDto>>.Ok(
            containers.Select(MapContainer).ToList());
    }

    public async Task<ServiceResult<AdminServerDto>> CreateServerAsync(
        string endpointUrl,
        string? description,
        bool isMonitoringEnabled,
        CancellationToken cancellationToken = default)
    {
        ServiceError? error = administratorAccessPolicy.GetAccessError() ?? ValidateSettings(endpointUrl, description);
        if (error is not null)
        {
            return ServiceResult<AdminServerDto>.Fail(error);
        }

        MonitoredServer server = new(Guid.NewGuid(), endpointUrl, description, isMonitoringEnabled);
        unitOfWork.MonitoredServers.Create(server);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await changePublisher.PublishObjectsChangedAsync(cancellationToken);
        return ServiceResult<AdminServerDto>.Ok(MapServer(server));
    }

    public async Task<ServiceResult<AdminServerDto>> UpdateServerAsync(
        Guid serverId,
        string endpointUrl,
        string? description,
        bool isMonitoringEnabled,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ServiceError? error = administratorAccessPolicy.GetAccessError() ?? ValidateSettings(endpointUrl, description);
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

        server.UpdateSettings(endpointUrl, description, isMonitoringEnabled);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (PersistenceConcurrencyException)
        {
            return ServiceResult<AdminServerDto>.Fail(CreateConcurrencyError());
        }
        await changePublisher.PublishObjectsChangedAsync(cancellationToken);
        return ServiceResult<AdminServerDto>.Ok(MapServer(server));
    }

    public async Task<ServiceResult<AdminContainerDto>> CreateContainerAsync(
        Guid serverId,
        string endpointUrl,
        string? description,
        bool isMonitoringEnabled,
        CancellationToken cancellationToken = default)
    {
        ServiceError? error = administratorAccessPolicy.GetAccessError() ?? ValidateSettings(endpointUrl, description);
        if (error is not null)
        {
            return ServiceResult<AdminContainerDto>.Fail(error);
        }

        MonitoredServer? server = await unitOfWork.MonitoredServers.GetItemByIdAsync(serverId, cancellationToken);
        if (server is null)
        {
            return ServiceResult<AdminContainerDto>.Fail(
                ServiceErrorType.Validation,
                "Родительский сервер не существует.",
                "serverId");
        }

        MonitoredContainer container = new(
            Guid.NewGuid(),
            server.Id,
            endpointUrl,
            description,
            isMonitoringEnabled);
        unitOfWork.MonitoredContainers.Create(container);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await changePublisher.PublishObjectsChangedAsync(cancellationToken);
        return ServiceResult<AdminContainerDto>.Ok(MapContainer(container));
    }

    public async Task<ServiceResult<AdminContainerDto>> UpdateContainerAsync(
        Guid containerId,
        string endpointUrl,
        string? description,
        bool isMonitoringEnabled,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ServiceError? error = administratorAccessPolicy.GetAccessError() ?? ValidateSettings(endpointUrl, description);
        if (error is not null)
        {
            return ServiceResult<AdminContainerDto>.Fail(error);
        }

        MonitoredContainer? container =
            await unitOfWork.MonitoredContainers.GetItemByIdAsync(containerId, cancellationToken);
        if (container is null)
        {
            return ServiceResult<AdminContainerDto>.Fail(ServiceErrorType.NotFound, "Контейнер не найден.");
        }

        if (container.Version != expectedVersion)
        {
            return ServiceResult<AdminContainerDto>.Fail(CreateConcurrencyError());
        }

        MonitoredServer? server =
            await unitOfWork.MonitoredServers.GetItemByIdAsync(container.ServerId, cancellationToken);
        if (server is null)
        {
            return ServiceResult<AdminContainerDto>.Fail(
                ServiceErrorType.Validation,
                "Родительский сервер не существует.",
                "serverId");
        }

        container.UpdateSettings(endpointUrl, description, isMonitoringEnabled);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
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

        IReadOnlyList<MonitoredContainer> containers =
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
                "Количество контейнеров не может быть отрицательным.",
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

                    IReadOnlyList<MonitoredContainer> containers =
                        await unitOfWork.MonitoredContainers.GetByServerIdAsync(
                            serverId,
                            transactionCancellationToken);
                    if (containers.Count != expectedContainerCount)
                    {
                        operationResult = ServiceResult.Fail(
                            ServiceErrorType.Conflict,
                            "Количество контейнеров изменилось. Повторите подтверждение удаления.");
                        return;
                    }

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

        MonitoredContainer? container =
            await unitOfWork.MonitoredContainers.GetItemByIdAsync(containerId, cancellationToken);
        if (container is null)
        {
            return ServiceResult.Fail(ServiceErrorType.NotFound, "Контейнер не найден.");
        }

        if (container.Version != expectedVersion)
        {
            return ServiceResult.Fail(CreateConcurrencyError());
        }

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

    private static AdminServerDto MapServer(MonitoredServer server)
    {
        return new AdminServerDto(
            server.Id,
            server.EndpointUrl,
            server.Description,
            server.IsMonitoringEnabled,
            server.LastKnownName,
            server.Status,
            server.ConsecutiveFailures,
            server.Version);
    }

    private static AdminContainerDto MapContainer(MonitoredContainer container)
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
            container.Version);
    }

    private static ServiceError CreateConcurrencyError()
    {
        return new ServiceError(
            ServiceErrorType.Conflict,
            "Объект был изменён другим процессом. Обновите данные и повторите операцию.");
    }
}
