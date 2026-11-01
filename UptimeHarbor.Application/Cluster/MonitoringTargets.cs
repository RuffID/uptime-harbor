using System.Collections.Immutable;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

public static class MonitoringTargets
{
    public static MonitoringTarget? Find(RevisionImage image, Guid id)
    {
        if (image.Monitoring.Nodes.TryGetValue(id, out Guid storage))
        {
            ImmutableArray<Guid> observers = image.Monitoring.Nodes.Keys.Where(node => node != id).Order().ToImmutableArray();
            return new(id, storage, 1, true, observers, 6, 5, 3, DateTimeOffset.UnixEpoch);
        }

        if (!image.Values.TryGetValue(id, out AcceptedValue? cell) || cell.Deleted || !cell.Value.Enabled
            || cell.Kind is not (DeletedEntityKind.Server or DeletedEntityKind.Container))
        {
            return null;
        }

        ConfigurationValue value = cell.Value;
        return new(id, value.Generation, cell.Revision, false, value.ObserverNodes,
            value.FailureThreshold, value.IntervalSeconds, value.TimeoutSeconds, value.AssignedAtUtc);
    }

    public static string Frontier(RevisionImage image, MonitoringTarget target)
    {
        var streams = image.Monitoring.Streams.Values.Where(stream => stream.TargetId == target.Id
            && stream.CheckGeneration == target.CheckGeneration && target.Observers.Contains(stream.ObserverNodeId))
            .OrderBy(stream => stream.ObserverNodeId).ThenBy(stream => stream.StorageGeneration)
            .Select(stream => new { stream.Id, stream.Sequence }).ToArray();
        return MonitoringKeys.Digest(streams);
    }

    public static TimeSpan Freshness(MonitoringTarget target)
    {
        return TimeSpan.FromSeconds(2 * target.IntervalSeconds + target.TimeoutSeconds + 20);
    }
}
