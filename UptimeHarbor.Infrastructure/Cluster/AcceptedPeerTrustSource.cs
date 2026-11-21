using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Секретов нет; отозванный credential никогда не заменяется старым bootstrap allowlist.</summary>
public class AcceptedPeerTrustSource(RevisionStateStore state, TimeProvider time) : IPeerTrustSource
{
    public async ValueTask<PeerCredential?> FindAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        IReadOnlyList<PeerCredential> credentials = await ListAsync(cancellationToken);
        return credentials.SingleOrDefault(item => item.CredentialId == credentialId);
    }

    public ValueTask<IReadOnlyList<PeerCredential>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RevisionImage? image = state.Current;
        if (image is null)
        {
            return ValueTask.FromResult<IReadOnlyList<PeerCredential>>([]);
        }

        ClusterOperationsImage operations = image.Operations;
        List<PeerCredential> result = [];
        DateTimeOffset now = time.GetUtcNow();
        foreach (OperationalCredential credential in operations.Credentials.Values)
        {
            OperationalNode? node = operations.Nodes.GetValueOrDefault(credential.NodeId);
            if (node is null || !EnrollmentPeerTrustPolicy.IsActive(image, node, credential, now))
            {
                continue;
            }

            bool admission = credential.AdmissionId.HasValue && !credential.ConsumedAtUtc.HasValue
                && credential.AdmissionExpiresAtUtc > now;
            if (!node.Voting && !admission)
            {
                continue;
            }

            result.Add(new(credential.Id, node.NodeId, node.StorageGeneration, new Uri(node.Address),
                credential.Fingerprint, node.Voting, admission, credential.ExpiresAtUtc));
        }

        return ValueTask.FromResult<IReadOnlyList<PeerCredential>>(ReferenceEquals(image, state.Current) ? result : []);
    }
}
