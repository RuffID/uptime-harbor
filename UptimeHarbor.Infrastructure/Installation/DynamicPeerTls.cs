using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Атомарный credential/trust snapshot; пустой provider закрывает handshake.</summary>
public class DynamicPeerTls(InstallationState state, RevisionStateStore? revisions = null, TimeProvider? time = null,
    EnrollmentProbeSession? probes = null)
{
    private Snapshot? current;

    public X509Certificate2? Certificate
    {
        get
        {
            Snapshot? snapshot = Current();
            return snapshot?.Certificate ?? probes?.Certificate;
        }
    }

    public bool Validate(X509Certificate2 certificate)
    {
        Snapshot? snapshot = Current();
        if (snapshot is null)
        {
            return probes?.Validate(certificate) == true;
        }

        if (snapshot?.DedicatedIssuerEpoch.HasValue == true && revisions?.Current?.OnboardingContracts?.GenesisInitialized == true)
        {
            return EnrollmentLiveTrust.Validate(revisions.Current, certificate, (time ?? TimeProvider.System).GetUtcNow());
        }

        if (snapshot is null || !snapshot.Pins.Contains(certificate.GetCertHashString(HashAlgorithmName.SHA256)))
        {
            return false;
        }

        using X509Chain chain = new();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(snapshot.Root);
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.Offline;
        if (snapshot.DedicatedIssuerEpoch.HasValue)
        {
            // Только dedicated application CA. Legacy Direct продолжает требовать offline CRL.
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        }

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

        if (snapshot.DedicatedIssuerEpoch.HasValue)
        {
            if (revisions?.Current?.OnboardingContracts?.GenesisInitialized == true)
            {
                OperationalCredential? accepted = revisions.Current.Operations.Credentials.GetValueOrDefault(credential.CredentialId);
                OperationalNode? node = revisions.Current.Operations.Nodes.GetValueOrDefault(credential.NodeId);
                return accepted is not null && node is not null && accepted.NodeId == node.NodeId
                    && node.StorageGeneration == credential.StorageGeneration && node.Address == credential.Address.AbsoluteUri
                    && accepted.Fingerprint == credential.Fingerprint && accepted.Fingerprint == EnrollmentCertificates.Hash(certificate.RawData)
                    && now < credential.ExpiresAtUtc;
            }

            string spki = EnrollmentCertificates.Hash(certificate.PublicKey.ExportSubjectPublicKeyInfo());
            EnrollmentRequest request = new(1, options.ClusterId, options.Generation, credential.CredentialId,
                credential.CredentialId, credential.NodeId, credential.StorageGeneration, credential.Address.AbsoluteUri,
                spki, new string('0', 64));
            EnrollmentIssuance template = new(1, credential.CredentialId, snapshot.DedicatedIssuerEpoch.Value,
                certificate.SerialNumber, credential.NodeId, credential.StorageGeneration, credential.Address.AbsoluteUri,
                spki, request.CsrDigest, new DateTimeOffset(certificate.NotBefore.ToUniversalTime()),
                new DateTimeOffset(certificate.NotAfter.ToUniversalTime()));
            return now < credential.ExpiresAtUtc && EnrollmentCertificates.ValidateLeaf(certificate, snapshot.Root,
                request, template, credential.Fingerprint, now, false);
        }

        // Legacy HTTP повторяет fingerprint, EKU, SAN и expiry; PEM из старого config не нужен.
        return new PeerCertificateValidator().Validate(certificate, snapshot.Root, options, credential, now,
            offlineRevocation: true);
    }

    public void Install(long generation, X509Certificate2 certificate, X509Certificate2 root,
        IEnumerable<string> acceptedPins, Func<X509Certificate2, bool> acceptedValidator, Guid? dedicatedIssuerEpoch = null)
    {
        LocalInstallationProfile? profile = state.Current;
        if (profile is null || generation != profile.RuntimeGeneration || !certificate.HasPrivateKey
            || profile.TrustAnchorFingerprint != root.GetCertHashString(HashAlgorithmName.SHA256))
        {
            throw new InvalidOperationException("StalePeerCredentialGeneration");
        }

        Volatile.Write(ref current, new(generation, certificate, root,
            acceptedPins.ToHashSet(StringComparer.OrdinalIgnoreCase), acceptedValidator, dedicatedIssuerEpoch));
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
        HashSet<string> Pins, Func<X509Certificate2, bool> AcceptedValidator, Guid? DedicatedIssuerEpoch);
}
