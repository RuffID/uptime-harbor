using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Только целый прежний storage с sealed checkpoint. Не требует работающего большинства до native Start.</summary>
public class ManagedRestartPreparation(ManagedStorageSeals seals, RevisionSnapshotStore snapshots,
    IRevisionProjectionStore projection, InstallationState installation, InstallationLease lease,
    LocalSecretStore secrets, ManagedAdmissionState admission, DynamicPeerTls tls,
    IOptions<ClusterOptions> cluster, TimeProvider time)
{
    public async Task<bool> PrepareAsync(LocalInstallationProfile profile, CancellationToken token)
    {
        if (profile != installation.Current || !profile.ManagedRestart || profile.BootId != lease.BootId
            || profile.Phase != InstallationPhase.ClusterQuarantine)
        {
            return false;
        }

        ManagedStorageSeal seal = seals.Read(profile);
        ProjectionHead sql = await projection.ReadHeadAsync(token) ?? throw new InvalidDataException("ManagedSqlHeadMissing");
        if (sql.ClusterId != seal.ClusterId || sql.Generation != seal.Generation || sql.Index < seal.Index
            || sql.Term < seal.Term || sql.Index == seal.Index && (sql.Term != seal.Term || sql.Digest != seal.HeadDigest))
        {
            throw new InvalidDataException("ManagedSqlHeadRollbackOrMismatch");
        }

        PublishedRevisionSnapshot snapshot = await snapshots.LoadAsync(seal.SnapshotId, seal.SnapshotDigest, token);
        RevisionImage image = snapshot.Image;
        OperationalNode node = image.Operations.Nodes[seal.NodeId];
        OperationalCredential credential = image.Operations.Credentials[seal.CredentialId];
        if (image.Index != seal.Index || image.Term != seal.Term || image.Digest != seal.HeadDigest
            || image.Operations.MembershipVersion != seal.MembershipVersion || node.Removed || !node.Voting
            || node.StorageGeneration != seal.StorageGeneration || node.Address != seal.PeerAddress
            || image.Operations.MaintenanceId != Guid.Empty || credential.NodeId != node.NodeId
            || credential.RevokedAtUtc.HasValue || time.GetUtcNow() >= credential.ExpiresAtUtc
            || image.RecoveryBaseline is not null || image.OnboardingContracts?.GenesisInitialized != true)
        {
            throw new InvalidDataException("ManagedAcceptedIdentityInvalid");
        }

        using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(credential.PublicCertificate));
        if (!EnrollmentLiveTrust.Validate(image, leaf, time.GetUtcNow()))
        {
            throw new InvalidDataException("ManagedCredentialInvalid");
        }

        AcceptedEnrollmentIssuer issuer = image.OnboardingContracts.Issuers!.Values.Single(item =>
            item.CertificateDigest == profile.TrustAnchorFingerprint && !item.RevokedAtUtc.HasValue);
        using X509Certificate2 root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(issuer.CertificateDer));
        byte[] pem = secrets.Read(profile.AttemptId!.Value, "peer", profile.KeyReference!);
        X509Certificate2 certificate;
        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportFromPem(Encoding.UTF8.GetString(pem));
            certificate = leaf.CopyWithPrivateKey(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pem);
        }

        RecoveryAdmission proof = new(image.ClusterId, image.Generation, node.NodeId, node.StorageGeneration,
            false, false, seal.SnapshotId, seal.SnapshotDigest, sql.Index, seal.MembershipVersion)
        {
            Proof = new ManagedRestartProof(lease.InstallIdentity, lease.BootId, lease.BootChallenge,
                RevisionCodec.Digest(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(seal)))
            {
                ActorId = ReadOriginalActor(profile)
            },
            Operations = image.Operations, MonitoringNodes = image.Monitoring.Nodes
        };
        admission.Publish(profile, proof);
        tls.Install(profile.RuntimeGeneration, certificate, new X509Certificate2(root),
            image.Operations.Credentials.Values.Select(item => item.Fingerprint),
            candidate => EnrollmentLiveTrust.Validate(image, candidate, time.GetUtcNow()), issuer.Epoch);
        return cluster.Value.Mode == "Restart";
    }

    private Guid ReadOriginalActor(LocalInstallationProfile profile)
    {
        byte[] account = secrets.Read(profile.AttemptId!.Value, "bootstrap", profile.BootstrapAccountReference
            ?? throw new InvalidDataException("ManagedBootstrapProvenanceMissing"));
        try
        {
            RevisionCommand command = RevisionCodec.Decode(account);
            if (command.Action != "BootstrapAccount" || RevisionCodec.Digest(command) != profile.BootstrapAccountDigest)
            {
                throw new InvalidDataException("ManagedBootstrapProvenanceChanged");
            }

            return command.ActorUserId;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(account);
        }
    }
}
