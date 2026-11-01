namespace UptimeHarbor.Application.Cluster.Models;

public record AcceptedMembershipChange(Guid ChangeId, Guid ActorUserId, Guid TargetNodeId,
    Uri Address, bool Remove, long ExpectedMembershipVersion, bool ActiveAdministratorConfirmed,
    bool TrustConfirmed, bool PreviousChangeReconciled);
