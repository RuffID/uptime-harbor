using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster;

public class PeerObservationCapture(RevisionStateStore accepted, IClusterStateView state, NodeReadiness readiness,
    IOptions<ClusterOptions> options, IServiceScopeFactory scopes, TimeProvider time)
{
    public async Task CaptureAsync(Guid targetNodeId, PeerHealthSnapshot? health, long durationMilliseconds,
        CancellationToken token)
    {
        if (!options.Value.Enabled || !readiness.CanWork(state) || !accepted.Available)
        {
            return;
        }

        MonitoringTarget? target = MonitoringTargets.Find(accepted.Current!, targetNodeId);
        if (target?.IsNode != true || !target.Observers.Contains(options.Value.NodeId))
        {
            return;
        }

        string? failure = "PeerUnavailable";
        bool confirmed = true;
        if (health is not null && health.NodeId == targetNodeId)
        {
            failure = null;
            foreach (string component in new[] { "Database", "Projection", "Monitoring", "Telegram" })
            {
                HealthResult? result = health.Components.GetValueOrDefault(component);
                if (result?.Status == "Unavailable")
                {
                    failure = Failure(component);
                    break;
                }

                if (result?.Status != "Ready")
                {
                    confirmed = false;
                }
            }
        }

        MonitoringObservation observation = new(Guid.NewGuid(), target.Id, options.Value.NodeId,
            options.Value.StorageGeneration, target.CheckGeneration, target.ConfigRevision, 1,
            time.GetUtcNow(), durationMilliseconds, failure is null, failure, null, null,
            confirmed && readiness.Get("Clock").Status == "Ready");
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ObservationCaptureService>().CaptureAsync(observation, token);
    }

    private static string Failure(string component)
    {
        if (component == "Database")
        {
            return "DatabaseUnavailable";
        }

        if (component == "Telegram")
        {
            return "TelegramUnavailable";
        }

        return "WorkerStalled";
    }
}
