using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Accepted cert другого узла не удостоверяет выбранный server endpoint.</summary>
public static class PeerServerPin
{
    public static async ValueTask<bool> MatchesAsync(Uri target, X509Certificate2 certificate,
        IPeerTrustSource trust, DateTimeOffset now, CancellationToken token)
    {
        if (!target.IsAbsoluteUri || target.Scheme != "https" || target.UserInfo.Length != 0
            || target.Query.Length != 0 || target.Fragment.Length != 0)
        {
            return false;
        }

        IReadOnlyList<PeerCredential> peers = await trust.ListAsync(token);
        PeerCredential[] candidates = peers.Where(peer => peer.Address.Scheme == "https"
            && peer.Address.Authority == target.Authority && peer.ExpiresAtUtc > now).Take(2).ToArray();
        if (candidates.Length != 1)
        {
            return false;
        }

        PeerCredential expected = candidates[0];
        string fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        if (!fingerprint.Equals(expected.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Повторяем registry lookup после I/O; established TLS не сохраняет отозванное право.
        PeerCredential? current = await trust.FindAsync(expected.CredentialId, token);
        return current == expected && current.ExpiresAtUtc > now;
    }
}
