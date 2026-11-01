using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster;

public class PeerProtocolGuard(ClusterOptions options, IPeerTrustSource trust, TimeProvider timeProvider)
{
    public async ValueTask<PeerCredential?> ValidateAsync(PeerEnvelope envelope, long bytes,
        long limit, CancellationToken cancellationToken)
    {
        bool wrongCluster = envelope.ClusterId != options.ClusterId || envelope.Generation != options.Generation;
        bool wrongIdentity = envelope.ToNodeId != options.NodeId || envelope.FromNodeId == options.NodeId
            || envelope.FromNodeId == Guid.Empty || envelope.RequestId == Guid.Empty || envelope.Challenge == Guid.Empty;
        if (wrongCluster || wrongIdentity || envelope.Compatibility != options.Compatibility || bytes < 0 || bytes > limit)
        {
            return null;
        }

        if ((timeProvider.GetUtcNow() - envelope.IssuedAtUtc).Duration() > TimeSpan.FromSeconds(30))
        {
            return null;
        }

        PeerCredential? credential = await trust.FindAsync(envelope.CredentialId, cancellationToken);
        if (credential is null || credential.NodeId != envelope.FromNodeId
            || credential.StorageGeneration != envelope.StorageGeneration
            || credential.ExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            return null;
        }

        return credential;
    }
}
