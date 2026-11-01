using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Monitoring.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Infrastructure.Monitoring;

/// <summary>Проверяет только принятые назначения. Local capture не ждёт главного или Telegram.</summary>
public class ClusterMonitoringCycle(RevisionStateStore accepted, IClusterStateView state, NodeReadiness readiness,
    IOptions<ClusterOptions> options, IServiceScopeFactory scopes, IHttpClientFactory clients,
    MonitoringProgressWatchdog progress, TimeProvider time, ILogger<ClusterMonitoringCycle> logger)
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly ConcurrentDictionary<(Guid Id, Guid Generation), long> completed = new();

    public async Task RunOnceAsync(CancellationToken token)
    {
        if (!options.Value.Enabled || !readiness.CanWork(state) || !accepted.Available)
        {
            return;
        }

        await serial.WaitAsync(token);
        try
        {
            RevisionImage image = accepted.Current!;
            int failures = 0;
            AcceptedValue[] targets = image.Values.Values.Where(cell => !cell.Deleted && cell.Value.Enabled
                && cell.Kind is DeletedEntityKind.Server or DeletedEntityKind.Container).OrderBy(cell => cell.Id).ToArray();
            await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token }, async (cell, cancellation) =>
            {
                MonitoringTarget target = MonitoringTargets.Find(image, cell.Id)!;
                if (!target.Observers.Contains(options.Value.NodeId))
                {
                    return;
                }

                var key = (cell.Id, target.CheckGeneration);
                if (completed.TryGetValue(key, out long at) && time.GetElapsedTime(at) < TimeSpan.FromSeconds(target.IntervalSeconds))
                {
                    return;
                }

                if (!readiness.CanWork(state) || !accepted.Available)
                {
                    Interlocked.Increment(ref failures);
                    return;
                }

                try
                {
                    if (await CheckAsync(cell, target, cancellation))
                    {
                        completed[key] = time.GetTimestamp();
                    }
                    else
                    {
                        Interlocked.Increment(ref failures);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref failures);
                    readiness.Report("Monitoring", false, "WorkerStalled", TimeSpan.FromSeconds(30));
                    logger.LogWarning("Cluster monitoring cycle failed for {TargetId} on {NodeId}.", cell.Id, options.Value.NodeId);
                }
            });

            if (failures == 0 && readiness.CanWork(state) && accepted.Available)
            {
                progress.RecordCompletedCycle();
            }
        }
        finally
        {
            serial.Release();
        }
    }

    public async Task<bool> CheckAsync(AcceptedValue cell, MonitoringTarget target, CancellationToken token)
    {
        if (!readiness.CanWork(state) || !accepted.Available || !target.Observers.Contains(options.Value.NodeId))
        {
            return false;
        }

        long start = time.GetTimestamp();
        long recoveryVersion = readiness.RecoveryVersion;
        MonitoringOptions checkOptions = new()
        {
            IntervalSeconds = target.IntervalSeconds, TimeoutSeconds = target.TimeoutSeconds,
            FailuresBeforeUnavailable = target.FailureThreshold
        };
        HttpMonitoringEndpointChecker checker = new(clients, Options.Create(checkOptions), time);
        MonitoringObservation observation;
        if (cell.Kind == DeletedEntityKind.Server)
        {
            ServerEndpointCheckResult result = await checker.CheckAsync(cell.Value.Endpoint!, token);
            observation = new(Guid.NewGuid(), cell.Id, options.Value.NodeId, options.Value.StorageGeneration,
                target.CheckGeneration, target.ConfigRevision, 1, result.CheckedAtUtc,
                (long)time.GetElapsedTime(start).TotalMilliseconds, result.Success, result.FailureKind?.ToString(),
                result.Name, result.StartedAtUtc, readiness.Get("Clock").Status == "Ready");
        }
        else
        {
            ContainerEndpointCheckResult result = await ((IContainerEndpointChecker)checker).CheckAsync(cell.Value.Endpoint!, token);
            observation = new(Guid.NewGuid(), cell.Id, options.Value.NodeId, options.Value.StorageGeneration,
                target.CheckGeneration, target.ConfigRevision, 1, result.CheckedAtUtc,
                (long)time.GetElapsedTime(start).TotalMilliseconds, result.Success, result.FailureKind?.ToString(),
                result.Name, result.StartedAtUtc, readiness.Get("Clock").Status == "Ready");
        }

        if (!readiness.CanWork(state) || recoveryVersion != readiness.RecoveryVersion)
        {
            return false;
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        CapturedObservation? captured = await scope.ServiceProvider.GetRequiredService<ObservationCaptureService>().CaptureAsync(observation, token);
        return captured is not null;
    }
}
