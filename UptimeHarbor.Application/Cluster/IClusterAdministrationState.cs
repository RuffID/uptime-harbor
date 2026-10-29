using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster;

public interface IClusterAdministrationState
{
    ValueTask<AcceptedMembershipChange?> FindAuthorizedChangeAsync(Guid userId, Guid changeId, CancellationToken cancellationToken);
    ValueTask<bool> ConfirmProtocolResultAsync(Guid changeId, long membershipVersion, CancellationToken cancellationToken);
}
