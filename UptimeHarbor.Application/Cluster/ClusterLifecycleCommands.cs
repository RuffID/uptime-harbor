using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

public static class ClusterLifecycleCommands
{
    public static RevisionCommand Create(RevisionImage image, Guid nodeId, Guid storage, string action,
        Guid entityId, ClusterOperation payload, DateTimeOffset atUtc)
    {
        if (action is not ("ConfirmMembership" or "OpenSenderEpoch" or "ActivateNode" or "LeaveMaintenance"))
        {
            throw new InvalidOperationException("NotAnInternalLifecycleAction");
        }

        return new RevisionCommand
        {
            ClusterId = image.ClusterId, ClusterGeneration = image.Generation, OperationId = Guid.NewGuid(),
            OriginNodeId = nodeId, ActorUserId = Guid.Empty, ActorRevision = 0, EntityId = entityId,
            EntityKind = DeletedEntityKind.Node, Action = action, BaseRevision = image.Operations.Revision,
            CreatedAtUtc = atUtc, SystemSource = "ClusterLifecycle",
            Operation = payload with { StorageGeneration = storage, CoordinatorTerm = image.Term }
        };
    }
}
