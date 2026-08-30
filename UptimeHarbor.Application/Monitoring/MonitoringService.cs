using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Monitoring.Models;
using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Application.Monitoring;

public class MonitoringService(
    IMonitoringUnitOfWork unitOfWork,
    IMonitoringEventPublisher eventPublisher)
    : IMonitoringService
{
    public async Task<IReadOnlyList<MonitoringTargetDto>> GetEnabledTargetsAsync(
        CancellationToken cancellationToken = default)
    {
        List<MonitoredServer> servers =
            await unitOfWork.MonitoredServers.GetEnabledAsync(cancellationToken);
        List<MonitoredContainer> containers =
            await unitOfWork.MonitoredContainers.GetEnabledAsync(cancellationToken);

        List<MonitoringTargetDto> targets = new(servers.Count + containers.Count);
        targets.AddRange(servers.Select(server => new MonitoringTargetDto(
            server.Id,
            MonitoringObjectKind.Server,
            server.EndpointUrl)));
        targets.AddRange(containers.Select(container => new MonitoringTargetDto(
            container.Id,
            MonitoringObjectKind.Container,
            container.EndpointUrl)));
        return targets;
    }

    public async Task<bool> RecordServerResultAsync(
        Guid serverId,
        string checkedEndpointUrl,
        ServerEndpointCheckResult result,
        int failuresBeforeUnavailable,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        MonitoredServer? server = await unitOfWork.MonitoredServers.GetItemByIdAsync(
            serverId,
            cancellationToken);
        if (!CanApplyResult(server, checkedEndpointUrl))
        {
            return false;
        }

        MonitoringTransition transition = result.Success
            ? server!.RecordSuccessfulCheck(
                result.Name ?? throw new InvalidOperationException("Успешная проверка сервера должна содержать имя."),
                result.CheckedAtUtc)
            : server!.RecordFailedCheck(result.CheckedAtUtc, failuresBeforeUnavailable);

        MonitoringStateChangedEvent monitoringEvent =
            CreateEvent(server, transition, result.Success, result.FailureKind);
        EnqueueNotificationIfRequired(monitoringEvent);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (PersistenceConcurrencyException)
        {
            return false;
        }

        await eventPublisher.PublishAsync(monitoringEvent, cancellationToken);
        return true;
    }

    public async Task<bool> RecordContainerResultAsync(
        Guid containerId,
        string checkedEndpointUrl,
        ContainerEndpointCheckResult result,
        int failuresBeforeUnavailable,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        MonitoredContainer? container = await unitOfWork.MonitoredContainers.GetItemByIdAsync(
            containerId,
            cancellationToken);
        if (!CanApplyResult(container, checkedEndpointUrl))
        {
            return false;
        }

        MonitoringTransition transition = result.Success
            ? container!.RecordSuccessfulCheck(
                result.Name ?? throw new InvalidOperationException("Успешная проверка контейнера должна содержать имя."),
                result.StartedAtUtc ?? throw new InvalidOperationException("Успешная проверка контейнера должна содержать время запуска."),
                result.CheckedAtUtc)
            : container!.RecordFailedCheck(result.CheckedAtUtc, failuresBeforeUnavailable);

        MonitoringStateChangedEvent monitoringEvent =
            CreateEvent(container, transition, result.Success, result.FailureKind);
        EnqueueNotificationIfRequired(monitoringEvent);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (PersistenceConcurrencyException)
        {
            return false;
        }

        await eventPublisher.PublishAsync(monitoringEvent, cancellationToken);
        return true;
    }

    private static bool CanApplyResult(MonitoredObject? monitoredObject, string checkedEndpointUrl)
    {
        return monitoredObject is not null
            && monitoredObject.IsMonitoringEnabled
            && string.Equals(
                monitoredObject.EndpointUrl,
                checkedEndpointUrl,
                StringComparison.Ordinal);
    }

    private void EnqueueNotificationIfRequired(MonitoringStateChangedEvent monitoringEvent)
    {
        if (monitoringEvent.Transition is MonitoringTransition.BecameUnavailable
            or MonitoringTransition.Recovered)
        {
            unitOfWork.EnqueueNotification(monitoringEvent);
        }
    }

    private static MonitoringStateChangedEvent CreateEvent(
        MonitoredServer server,
        MonitoringTransition transition,
        bool checkSucceeded,
        EndpointCheckFailureKind? failureKind)
    {
        return new MonitoringStateChangedEvent(
            server.Id,
            MonitoringObjectKind.Server,
            null,
            server.LastKnownName,
            server.Description,
            server.Status,
            server.CurrentIncidentId,
            server.ConsecutiveFailures,
            server.LastCheckedAtUtc
                ?? throw new InvalidOperationException("Сохранённый результат должен содержать время проверки."),
            null,
            transition,
            checkSucceeded,
            failureKind);
    }

    private static MonitoringStateChangedEvent CreateEvent(
        MonitoredContainer container,
        MonitoringTransition transition,
        bool checkSucceeded,
        EndpointCheckFailureKind? failureKind)
    {
        return new MonitoringStateChangedEvent(
            container.Id,
            MonitoringObjectKind.Container,
            container.ServerId,
            container.LastKnownName,
            container.Description,
            container.Status,
            container.CurrentIncidentId,
            container.ConsecutiveFailures,
            container.LastCheckedAtUtc
                ?? throw new InvalidOperationException("Сохранённый результат должен содержать время проверки."),
            container.StartedAtUtc,
            transition,
            checkSucceeded,
            failureKind);
    }
}
