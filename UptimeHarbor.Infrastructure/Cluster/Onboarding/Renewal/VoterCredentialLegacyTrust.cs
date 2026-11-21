using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

/// <summary>Политика настроенного ручного CA для принятого voter legacy Direct.</summary>
public class VoterCredentialLegacyTrust(ClusterOptions options)
{
    private readonly PeerCertificateValidator validator = new();
    private readonly Func<X509Certificate2, X509Certificate2, ClusterOptions, DateTimeOffset, bool>? validateChain;

    internal VoterCredentialLegacyTrust(ClusterOptions options,
        Func<X509Certificate2, X509Certificate2, ClusterOptions, DateTimeOffset, bool> validateChain) : this(options)
    {
        this.validateChain = validateChain;
    }

    internal bool Validate(RevisionImage image, OperationalNode node, OperationalCredential credential,
        X509Certificate2 leaf, DateTimeOffset now)
    {
        if (options.TlsBoundary != "Direct" || options.ClusterId != image.ClusterId || options.Generation != image.Generation
            || string.IsNullOrEmpty(options.ClientCaPath))
        {
            return false;
        }

        try
        {
            using X509Certificate2 root = X509CertificateLoader.LoadCertificateFromFile(options.ClientCaPath);

            PeerCredential peer = new(credential.Id, node.NodeId, node.StorageGeneration, new Uri(node.Address),
                credential.Fingerprint, true, false, credential.ExpiresAtUtc);
            if (validateChain is not null)
            {
                return validator.Validate(leaf, root, options, peer, now, validateChain);
            }

            return validator.Validate(leaf, root, options, peer, now);
        }
        catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
