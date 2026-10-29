using System.Collections.Immutable;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

/// <summary>Детерминированное сведение на принятом времени. Локальные часы reducer не используются.</summary>
public static class MonitoringAvailability
{
    public static MonitoringEvaluation Evaluate(RevisionImage image, MonitoringTarget target, DateTimeOffset atUtc)
    {
        List<Guid> failed = [];
        List<Guid> missing = [];
        int successes = 0;
        ImmutableDictionary<Guid, string>.Builder failureKinds = ImmutableDictionary.CreateBuilder<Guid, string>();
        ImmutableDictionary<Guid, MonitoringObservation>.Builder metadata = ImmutableDictionary.CreateBuilder<Guid, MonitoringObservation>();
        foreach (Guid observer in target.Observers.Order())
        {
            MonitoringObservation? confirmation = null;
            int failures = 0;
            long sequence = 0;
            Guid storage = image.Monitoring.Nodes.GetValueOrDefault(observer);
            IEnumerable<MonitoringObservation> observations = image.Monitoring.Observations.Values
                .Where(item => item.TargetId == target.Id && item.ObserverNodeId == observer
                    && item.StorageGeneration == storage && item.CheckGeneration == target.CheckGeneration
                    && item.ConfigRevision <= target.ConfigRevision && item.CheckedAtUtc <= atUtc && item.ClockConfirmed
                    && image.Monitoring.Acceptances.TryGetValue(item.Id, out ObservationAcceptance? acceptance) && !acceptance.HistoricalOnly)
                .OrderBy(item => item.Sequence);
            foreach (MonitoringObservation observation in observations)
            {
                if (observation.Sequence != sequence + 1)
                {
                    failures = 0;
                    confirmation = null;
                }

                sequence = observation.Sequence;
                if (observation.Succeeded)
                {
                    failures = 0;
                    confirmation = observation;
                    metadata[observer] = observation;
                }
                else
                {
                    failures++;
                    int threshold = target.FailureThreshold;
                    if (target.IsNode && observation.FailureKind != "PeerUnavailable")
                    {
                        threshold = 1;
                    }

                    if (failures >= threshold)
                    {
                        confirmation = observation;
                    }
                }
            }

            if (confirmation is null || atUtc - confirmation.CheckedAtUtc >= MonitoringTargets.Freshness(target))
            {
                missing.Add(observer);
            }
            else if (confirmation.Succeeded)
            {
                successes++;
            }
            else
            {
                failed.Add(observer);
                failureKinds.Add(observer, confirmation.FailureKind!);
            }
        }

        CoverageState coverage = CoverageState.Complete;
        if (missing.Count > 0)
        {
            coverage = CoverageState.Degraded;
            if (missing.Count == target.Observers.Length)
            {
                coverage = CoverageState.Lost;
            }

            if (atUtc - target.AssignedAtUtc < MonitoringTargets.Freshness(target))
            {
                coverage = CoverageState.Initializing;
            }
        }

        AvailabilityState availability = AvailabilityState.Unknown;
        if (successes > 0)
        {
            availability = AvailabilityState.Available;
            if (failed.Count > 0)
            {
                availability = AvailabilityState.Partial;
            }
        }
        else if (target.Observers.Length > 0 && failed.Count == target.Observers.Length)
        {
            availability = AvailabilityState.Unavailable;
        }

        return new(target.Id, target.CheckGeneration, availability, coverage, atUtc,
            MonitoringTargets.Frontier(image, target), failed.ToImmutableArray(), missing.ToImmutableArray(),
            metadata.ToImmutable(), image.Index, image.Term) { Failures = failureKinds.ToImmutable() };
    }
}
