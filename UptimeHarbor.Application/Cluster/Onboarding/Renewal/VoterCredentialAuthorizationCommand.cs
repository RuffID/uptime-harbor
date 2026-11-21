using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Onboarding.Renewal;

/// <summary>Exact immutable envelope; создание команды не является PoP или разрешением dispatcher.</summary>
public static class VoterCredentialAuthorizationCommand
{
    public static RevisionCommand Create(VoterCredentialRequest request)
    {
        EnrollmentIssuance leaf = request.Renewal.Issuance;
        RevisionCommand command = new()
        {
            Schema = 3, ClusterId = request.ClusterId, ClusterGeneration = request.Generation,
            OperationId = request.OperationId, OriginNodeId = leaf.NodeId, ActorUserId = Guid.Empty, ActorRevision = 0,
            EntityKind = DeletedEntityKind.Node, EntityId = leaf.NodeId, BaseRevision = request.Renewal.PreviousCredentialRevision,
            Action = "AuthorizeVoterCredential", CreatedAtUtc = leaf.NotAfterUtc.AddDays(-30), SystemSource = "VoterCredentialLifecycle",
            EnrollmentTrust = new(1, leaf.StorageGeneration, leaf.IssuerEpoch, null, null, null, null, 0, null)
            {
                Rotation = new(request.Renewal, null, null)
            }
        };
        VoterCredentialCommands.Validate(command);

        return command;
    }
}
