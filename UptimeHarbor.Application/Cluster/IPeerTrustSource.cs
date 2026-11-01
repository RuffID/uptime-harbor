using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster;

public interface IPeerTrustSource
{
    ValueTask<PeerCredential?> FindAsync(Guid credentialId, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<PeerCredential>> ListAsync(CancellationToken cancellationToken);
}
