using System.Collections.Immutable;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

public static class MonitoringCommands
{
    public static RevisionCommand Record(RevisionImage image, Guid nodeId, Guid storage,
        ImmutableArray<MonitoringObservation> observations)
    {
        MonitoringObservation first = observations[0];
        MonitoringObservation last = observations[^1];
        DeletedEntityKind kind = DeletedEntityKind.Server;
        if (image.Monitoring.Nodes.ContainsKey(first.TargetId))
        {
            kind = DeletedEntityKind.Node;
        }
        else if (image.Values.TryGetValue(first.TargetId, out AcceptedValue? target))
        {
            kind = target.Kind;
        }

        return new RevisionCommand
        {
            ClusterId = image.ClusterId, ClusterGeneration = image.Generation,
            OperationId = MonitoringKeys.Id($"batch/{nodeId:N}/{storage:N}/{first.Id:N}/{last.Id:N}"),
            OriginNodeId = nodeId, ActorUserId = Guid.Empty, ActorRevision = 0,
            EntityId = first.TargetId, EntityKind = kind, Action = "RecordObservations", BaseRevision = first.ConfigRevision,
            CreatedAtUtc = last.CheckedAtUtc, SystemSource = "MonitoringObserver",
            Monitoring = new MonitoringCommand { StorageGeneration = storage, Observations = observations }
        };
    }

    public static RevisionCommand Evaluate(RevisionImage image, MonitoringTarget target, Guid nodeId, Guid storage,
        long term, DateTimeOffset atUtc, bool historical, DateTimeOffset proposedAtUtc)
    {
        DeletedEntityKind kind = DeletedEntityKind.Node;
        if (!target.IsNode)
        {
            kind = image.Values[target.Id].Kind;
        }

        string frontier = MonitoringTargets.Frontier(image, target);
        return new RevisionCommand
        {
            ClusterId = image.ClusterId, ClusterGeneration = image.Generation,
            OperationId = MonitoringKeys.Id($"evaluate/{target.Id:N}/{target.CheckGeneration:N}/{term}/{atUtc:O}/{frontier}/{historical}"),
            OriginNodeId = nodeId, ActorUserId = Guid.Empty, ActorRevision = 0,
            EntityId = target.Id, EntityKind = kind, Action = "EvaluateAvailability", BaseRevision = target.ConfigRevision,
            CreatedAtUtc = atUtc, SystemSource = "MonitoringCoordinator",
            Monitoring = new MonitoringCommand
            {
                StorageGeneration = storage, CoordinatorTerm = term, EvaluationTimeUtc = atUtc,
                FrontierDigest = frontier, Historical = historical
            }
        };
    }
}
