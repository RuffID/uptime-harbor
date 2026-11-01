using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster;

public class UnavailablePeerTrustSource : IPeerTrustSource
{
    public ValueTask<PeerCredential?> FindAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<PeerCredential?>(null);
    }

    public ValueTask<IReadOnlyList<PeerCredential>> ListAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<IReadOnlyList<PeerCredential>>([]);
    }
}
