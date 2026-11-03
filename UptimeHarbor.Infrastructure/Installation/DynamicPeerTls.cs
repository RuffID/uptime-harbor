using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Атомарный credential/trust snapshot; пустой provider закрывает handshake.</summary>
public class DynamicPeerTls(InstallationState state)
{
    private Snapshot? current;

    public X509Certificate2? Certificate
    {
        get
        {
            Snapshot? snapshot = Current();
            return snapshot?.Certificate;
        }
    }

    public bool Validate(X509Certificate2 certificate)
    {
        Snapshot? snapshot = Current();
        if (snapshot is null || !snapshot.Pins.Contains(certificate.GetCertHashString(HashAlgorithmName.SHA256)))
        {
            return false;
        }

        using X509Chain chain = new();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(snapshot.Root);
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.Offline;
        // Dedicated issuer revocation/purpose/SAN проверяет accepted provider этапа 04.
        return snapshot.AcceptedValidator(certificate) && chain.Build(certificate);
    }

    public bool ValidateRequest(X509Certificate2 certificate, ClusterOptions options,
        PeerCredential credential, DateTimeOffset now)
    {
        Snapshot? snapshot = Current();
        if (snapshot is null || !Validate(certificate))
        {
            return false;
        }

        // HTTP повторяет accepted fingerprint, EKU, SAN и expiry; PEM из старого config не нужен.
        return new PeerCertificateValidator().Validate(certificate, snapshot.Root, options, credential, now,
            offlineRevocation: true);
    }

    public void Install(long generation, X509Certificate2 certificate, X509Certificate2 root,
        IEnumerable<string> acceptedPins, Func<X509Certificate2, bool> acceptedValidator)
    {
        LocalInstallationProfile? profile = state.Current;
        if (profile is null || generation != profile.RuntimeGeneration || !certificate.HasPrivateKey
            || profile.TrustAnchorFingerprint != root.GetCertHashString(HashAlgorithmName.SHA256))
        {
            throw new InvalidOperationException("StalePeerCredentialGeneration");
        }

        Volatile.Write(ref current, new(generation, certificate, root,
            acceptedPins.ToHashSet(StringComparer.OrdinalIgnoreCase), acceptedValidator));
    }

    public void Revoke()
    {
        Volatile.Write(ref current, null);
    }

    private Snapshot? Current()
    {
        LocalInstallationProfile? profile = state.Current;
        Snapshot? snapshot = Volatile.Read(ref current);
        if (profile is null || snapshot?.Generation != profile.RuntimeGeneration
            || profile.Phase is not (InstallationPhase.ClusterQuarantine or InstallationPhase.CatchingUp
                or InstallationPhase.AwaitingActivation or InstallationPhase.ClusterReady))
        {
            return null;
        }

        return snapshot;
    }

    private record Snapshot(long Generation, X509Certificate2 Certificate, X509Certificate2 Root,
        HashSet<string> Pins, Func<X509Certificate2, bool> AcceptedValidator);
}
