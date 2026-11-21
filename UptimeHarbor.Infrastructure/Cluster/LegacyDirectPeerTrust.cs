using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Выбирает одну политику доверия для соединения или запроса legacy Direct.</summary>
public static class LegacyDirectPeerTrust
{
    public static bool ValidateClient(X509Certificate2 leaf, ClusterOptions options, RevisionImage? image,
        DateTimeOffset now, PeerCredential? peer = null)
    {
        if (options.TlsBoundary != "Direct")
        {
            return false;
        }

        try
        {
            bool? dedicated = ValidateDedicated(leaf, image, now, server: false, peer);
            if (dedicated.HasValue)
            {
                return dedicated.Value && image is not null && image.ClusterId == options.ClusterId && image.Generation == options.Generation;
            }

            using X509Certificate2 root = X509CertificateLoader.LoadCertificateFromFile(options.ClientCaPath);
            PeerCertificateValidator validator = new();
            if (peer is null)
            {
                return validator.ValidateChain(leaf, root, options, now);
            }

            return validator.Validate(leaf, root, options, peer, now);
        }
        catch (Exception error) when (error is CryptographicException or FormatException or InvalidDataException
            or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // null выбирает политику ручного CA. Отказ dedicated завершает проверку.
    internal static bool? ValidateDedicated(X509Certificate2 leaf, RevisionImage? image, DateTimeOffset now, bool server,
        PeerCredential? peer = null)
    {
        string fingerprint = EnrollmentCertificates.Hash(leaf.RawData);
        OperationalCredential? credential = null;
        if (peer is not null)
        {
            credential = image?.Operations.Credentials.GetValueOrDefault(peer.CredentialId);
            if (!fingerprint.Equals(peer.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        else if (image is not null)
        {
            OperationalCredential[] matches = image.Operations.Credentials.Values
                .Where(item => fingerprint.Equals(item.Fingerprint, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matches.Length > 1)
            {
                return false;
            }

            credential = matches.SingleOrDefault();
        }

        if (!VoterCredentialLeafTrust.RequiresDedicatedTrust(image, credential, leaf))
        {
            return null;
        }

        if (image is null || credential is null || credential.Fingerprint != fingerprint)
        {
            return false;
        }

        OperationalNode? node = image.Operations.Nodes.GetValueOrDefault(credential.NodeId);
        if (node is not { Voting: true } || peer is not null && (peer.NodeId != node.NodeId
            || peer.StorageGeneration != node.StorageGeneration || peer.Address.AbsoluteUri != node.Address
            || !peer.IsVoting || peer.IsAdmission || peer.ExpiresAtUtc != credential.ExpiresAtUtc))
        {
            return false;
        }

        return VoterCredentialLeafTrust.ValidateDedicated(image, credential, leaf, now, server);
    }
}
