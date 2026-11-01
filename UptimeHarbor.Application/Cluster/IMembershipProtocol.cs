using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster;

public interface IMembershipProtocol
{
    ValueTask<MembershipSnapshot?> ReadAsync(CancellationToken cancellationToken);
    ValueTask<bool> ChangeAsync(AcceptedMembershipChange change, CancellationToken cancellationToken);
}
