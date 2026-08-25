using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Monitoring.Models;

namespace UptimeHarbor.Infrastructure.Monitoring;

public class MonitoringCycleRunner(
    IServiceScopeFactory scopeFactory,
    IOptions<MonitoringOptions> options,
    ILogger<MonitoringCycleRunner> logger)
    : IMonitoringCycleRunner
{
    private readonly ConcurrentDictionary<string, byte> activeChecks = new();

    public async Task RunCycleAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MonitoringTargetDto> targets;
        await using (AsyncServiceScope scope = scopeFactory.CreateAsyncScope())
        {
            IMonitoringService monitoringService =
                scope.ServiceProvider.GetRequiredService<IMonitoringService>();
            targets = await monitoringService.GetEnabledTargetsAsync(cancellationToken);
        }

        Task[] checks = targets
            .Select(target => CheckSafelyAsync(target, cancellationToken))
            .ToArray();
        await Task.WhenAll(checks);
    }

    private async Task CheckSafelyAsync(
        MonitoringTargetDto target,
        CancellationToken cancellationToken)
    {
        string key = $"{target.ObjectKind}:{target.Id}";
        if (!activeChecks.TryAdd(key, 0))
        {
            return;
        }

        try
        {
            await CheckAsync(target, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Monitoring check failed unexpectedly for {ObjectKind} {ObjectId}.",
                target.ObjectKind,
                target.Id);
        }
        finally
        {
            activeChecks.TryRemove(key, out _);
        }
    }

    private async Task CheckAsync(
        MonitoringTargetDto target,
        CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IMonitoringService monitoringService =
            scope.ServiceProvider.GetRequiredService<IMonitoringService>();

        switch (target.ObjectKind)
        {
            case MonitoringObjectKind.Server:
                IServerEndpointChecker serverChecker =
                    scope.ServiceProvider.GetRequiredService<IServerEndpointChecker>();
                ServerEndpointCheckResult serverResult = await serverChecker.CheckAsync(
                    target.EndpointUrl,
                    cancellationToken);
                await monitoringService.RecordServerResultAsync(
                    target.Id,
                    target.EndpointUrl,
                    serverResult,
                    options.Value.FailuresBeforeUnavailable,
                    cancellationToken);
                break;

            case MonitoringObjectKind.Container:
                IContainerEndpointChecker containerChecker =
                    scope.ServiceProvider.GetRequiredService<IContainerEndpointChecker>();
                ContainerEndpointCheckResult containerResult = await containerChecker.CheckAsync(
                    target.EndpointUrl,
                    cancellationToken);
                await monitoringService.RecordContainerResultAsync(
                    target.Id,
                    target.EndpointUrl,
                    containerResult,
                    options.Value.FailuresBeforeUnavailable,
                    cancellationToken);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(target.ObjectKind),
                    target.ObjectKind,
                    "Неизвестный тип объекта мониторинга.");
        }
    }
}
