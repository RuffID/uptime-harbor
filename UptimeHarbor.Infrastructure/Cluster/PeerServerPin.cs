using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Accepted cert другого узла не удостоверяет выбранный server endpoint.</summary>
public static class PeerServerPin
{
    internal record ChainProof(string Digest, Func<DateTimeOffset, bool> Validate);

    internal record Observation(PeerCredential Credential, DateTimeOffset NotBefore, DateTimeOffset NotAfter)
    {
        public ChainProof? Chain { get; init; }

        public bool IsValidAt(DateTimeOffset now)
        {
            return now >= NotBefore && now < NotAfter && now < Credential.ExpiresAtUtc;
        }

        public bool Matches(X509Certificate certificate)
        {
            return certificate.GetCertHashString(HashAlgorithmName.SHA256)
                .Equals(Credential.Fingerprint, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static async ValueTask<bool> MatchesAsync(Uri target, X509Certificate2 certificate,
        IPeerTrustSource trust, DateTimeOffset now, CancellationToken token)
    {
        return await ResolveCoreAsync(target, certificate, trust, now, allowOverlap: false, token) is not null;
    }

    public static ValueTask<PeerCredential?> ResolvePresentedAsync(Uri target, X509Certificate2 certificate,
        IPeerTrustSource trust, DateTimeOffset now, CancellationToken token)
    {
        return ResolveCoreAsync(target, certificate, trust, now, allowOverlap: true, token);
    }

    private static async ValueTask<PeerCredential?> ResolveCoreAsync(Uri target, X509Certificate2 certificate,
        IPeerTrustSource trust, DateTimeOffset now, bool allowOverlap, CancellationToken token)
    {
        if (!target.IsAbsoluteUri || target.Scheme != "https" || target.UserInfo.Length != 0
            || target.Query.Length != 0 || target.Fragment.Length != 0
            || now.UtcDateTime < certificate.NotBefore.ToUniversalTime() || now.UtcDateTime >= certificate.NotAfter.ToUniversalTime())
        {
            return null;
        }

        string fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        IReadOnlyList<PeerCredential> peers = await trust.ListAsync(token);
        PeerCredential[] matches = peers.Where(peer => peer.ExpiresAtUtc > now
            && fingerprint.Equals(peer.Fingerprint, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (matches.Length != 1)
        {
            return null;
        }

        PeerCredential expected = matches[0];
        if (expected.Address.Scheme != "https" || expected.Address.Authority != target.Authority
            || peers.Any(peer => peer.Address.Scheme == "https" && peer.Address.Authority == target.Authority
                && peer.ExpiresAtUtc > now && peer != expected && (!allowOverlap || peer.NodeId != expected.NodeId
                    || peer.StorageGeneration != expected.StorageGeneration || peer.Address != expected.Address)))
        {
            return null;
        }

        // Повторяем registry lookup после I/O; established TLS не сохраняет отозванное право.
        PeerCredential? current = await trust.FindAsync(expected.CredentialId, token);
        return current == expected ? current : null;
    }
}
