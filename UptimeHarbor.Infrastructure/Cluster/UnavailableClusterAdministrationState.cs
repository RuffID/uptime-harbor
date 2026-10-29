using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster;

public class UnavailableClusterAdministrationState : IClusterAdministrationState
{
    public ValueTask<AcceptedMembershipChange?> FindAuthorizedChangeAsync(Guid userId, Guid changeId, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<AcceptedMembershipChange?>(null);
    }

    public ValueTask<bool> ConfirmProtocolResultAsync(Guid changeId, long membershipVersion, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Accepted administration state is unavailable.");
    }
}
