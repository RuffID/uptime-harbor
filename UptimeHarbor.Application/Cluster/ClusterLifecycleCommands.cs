using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

public static class ClusterLifecycleCommands
{
    public static RevisionCommand? OpenSenderEpoch(RevisionImage image, ConsensusStatus leader, Guid storage, DateTimeOffset atUtc)
    {
        if (image.Operations.AssignedSenderNodeId is not Guid selected || !leader.VotingNodes.Contains(selected))
        {
            return null;
        }

        SenderAuthorityKey? current = image.Operations.Sender;
        if (current?.Term == leader.Term && current.MembershipVersion == leader.MembershipVersion
            && current.SenderNodeId == selected)
        {
            return null;
        }

        SenderAuthorityKey next = new(image.ClusterId, image.Generation, leader.MembershipVersion, leader.Term, selected, Guid.NewGuid());
        return Create(image, leader.NodeId, storage, "OpenSenderEpoch", selected,
            new ClusterOperation { Sender = next, MembershipVersion = leader.MembershipVersion }, atUtc, leader.Term);
    }

    public static RevisionCommand Create(RevisionImage image, Guid nodeId, Guid storage, string action,
        Guid entityId, ClusterOperation payload, DateTimeOffset atUtc, long? coordinatorTerm = null)
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
            Operation = payload with { StorageGeneration = storage, CoordinatorTerm = coordinatorTerm ?? image.Term }
        };
    }
}
