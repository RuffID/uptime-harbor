using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

public static class AdministrationMonitoring
{
    public static AdministrationTargetDto Read(RevisionImage image, Guid id, DateTimeOffset now)
    {
        AcceptedValue? cell = image.Values.GetValueOrDefault(id);
        MonitoringTarget? target = MonitoringTargets.Find(image, id);
        MonitoringEvaluation? accepted = image.Monitoring.Live.GetValueOrDefault(id);
        if (target is null || target.Observers.Length == 0)
        {
            return new(id, "Unknown", "Initializing", accepted?.AtUtc, true,
                (cell?.Value.ObserverNodes ?? []).Select(node => new AdministrationObserverDto(node, "Missing", null,
                    null, null, null, null, null)).ToArray());
        }

        // Это представление свежести принятых фактов, а не новое событие/инцидент на читающем узле.
        MonitoringEvaluation current = MonitoringAvailability.Evaluate(image, target, now);
        List<AdministrationObserverDto> observers = [];
        foreach (Guid observer in target.Observers.Order())
        {
            MonitoringObservation? latest = image.Monitoring.Observations.Values
                .Where(item => item.TargetId == id && item.ObserverNodeId == observer
                    && item.CheckGeneration == target.CheckGeneration && item.ClockConfirmed
                    && item.StorageGeneration == image.Monitoring.Nodes.GetValueOrDefault(observer)
                    && item.CheckedAtUtc <= now && image.Monitoring.Acceptances.TryGetValue(item.Id, out ObservationAcceptance? acceptance)
                    && !acceptance.HistoricalOnly)
                .OrderByDescending(item => item.Sequence).FirstOrDefault();
            string status = "Available";
            if (latest is null)
            {
                status = "Missing";
            }
            else if (now - latest.CheckedAtUtc >= MonitoringTargets.Freshness(target))
            {
                status = "Stale";
            }
            else if (current.MissingNodes.Contains(observer))
            {
                status = "Unconfirmed";
            }
            else if (current.ProblemNodes.Contains(observer))
            {
                status = "Unavailable";
            }

            MonitoringObservation? metadata = current.Metadata.GetValueOrDefault(observer);
            long? uptime = null;
            if (metadata?.StartedAtUtc is DateTimeOffset started && now >= started)
            {
                uptime = (long)(now - started).TotalSeconds;
            }

            observers.Add(new(observer, status, latest?.CheckedAtUtc, SafeFailure(latest?.FailureKind),
                metadata?.EndpointName, metadata?.StartedAtUtc, metadata?.CheckedAtUtc, uptime));
        }

        bool stale = accepted is null || accepted.CheckGeneration != target.CheckGeneration
            || now - accepted.AtUtc >= MonitoringTargets.Freshness(target);
        return new(id, current.Availability.ToString(), current.Coverage.ToString(), accepted?.AtUtc, stale, observers);
    }

    private static string? SafeFailure(string? failure)
    {
        if (failure is null)
        {
            return null;
        }

        if (failure is "HttpStatus" or "Timeout" or "Network" or "InvalidJson" or "InvalidPayload"
            or "ResponseTooLarge" or "PeerUnavailable" or "DatabaseUnavailable" or "WorkerStalled" or "TelegramUnavailable")
        {
            return failure;
        }

        return "CheckFailed";
    }
}
