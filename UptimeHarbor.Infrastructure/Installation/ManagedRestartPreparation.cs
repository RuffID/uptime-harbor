using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation.Renewal;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Только целый прежний storage с sealed checkpoint. Не требует работающего большинства до native Start.</summary>
public class ManagedRestartPreparation(ManagedStorageSeals seals, RevisionSnapshotStore snapshots,
    IRevisionProjectionStore projection, InstallationState installation, InstallationLease lease,
    LocalSecretStore secrets, ManagedAdmissionState admission, DynamicPeerTls tls,
    IOptions<ClusterOptions> cluster, TimeProvider time, RevisionStateStore revisions,
    LocalVoterCredentialPublication publication)
{
    public async Task<bool> PrepareAsync(LocalInstallationProfile profile, CancellationToken token)
    {
        if (cluster.Value.Mode != "Restart")
        {
            return false;
        }

        if (profile != installation.Current || profile.Mode != "Bootstrap" || !profile.ManagedRestart || profile.BootId != lease.BootId
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

        ManagedWalValidation.RequireCheckpoint(cluster.Value.StoragePath, sql.Index, sql.Term);
        PublishedRevisionSnapshot snapshot = await snapshots.LoadAsync(seal.SnapshotId, seal.SnapshotDigest, token);
        RevisionImage image = snapshot.Image;
        OperationalNode node = image.Operations.Nodes[seal.NodeId];
        if (image.Index != seal.Index || image.Term != seal.Term || image.Digest != seal.HeadDigest
            || image.Operations.MembershipVersion != seal.MembershipVersion || node.Removed || !node.Voting
            || node.StorageGeneration != seal.StorageGeneration || node.Address != seal.PeerAddress
            || image.Operations.MaintenanceId != Guid.Empty
            || image.Operations.Credentials.GetValueOrDefault(seal.CredentialId)?.NodeId != node.NodeId
            || image.RecoveryBaseline is not null || image.OnboardingContracts?.GenesisInitialized != true)
        {
            throw new InvalidDataException("ManagedAcceptedIdentityInvalid");
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
        return true;
    }

    public void PreparePeerAfterReplay(LocalInstallationProfile profile)
    {
        try
        {
            PrepareReplayedCredential(profile);
        }
        catch
        {
            tls.BlockPublication();
            throw;
        }
    }

    private void PrepareReplayedCredential(LocalInstallationProfile profile)
    {
        long recoveryVersion = revisions.RecoveryVersion;
        RevisionImage image = revisions.Current ?? throw new InvalidDataException("ManagedReplayMissing");
        RecoveryAdmission? proof = admission.Read();
        if (cluster.Value.Mode != "Restart" || profile != installation.Current || !revisions.Available
            || proof?.Proof is not ManagedRestartProof || !admission.AllowsConsensus(image.Operations.MaintenanceId)
            || image.Index < proof.MinimumCommittedIndex || image.ClusterId != proof.ClusterId || image.Generation != proof.Generation
            || image.RecoveryBaseline is not null || image.OnboardingContracts?.GenesisInitialized != true
            || !image.Operations.Nodes.TryGetValue(proof.NodeId, out OperationalNode? node) || node.Removed || !node.Voting
            || node.StorageGeneration != profile.StorageGeneration || node.Address != profile.PeerAddress)
        {
            throw new InvalidDataException("ManagedReplayIdentityInvalid");
        }

        var rotation = VoterCredentialPolicy.Latest(image, node.NodeId);
        if (rotation?.ContinuationRequestId is not null)
        {
            throw new InvalidOperationException("VoterContinuationRecoveryRequired");
        }

        if (rotation is not null)
        {
            publication.PrepareQuarantine(profile, image, rotation, recoveryVersion);
            return;
        }

        var pending = publication.ReadPendingReplay(profile, image, profile.CredentialId!.Value, 0);
        OperationalCredential credential = image.Operations.Credentials[profile.CredentialId.Value];
        using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(credential.PublicCertificate));
        if (!EnrollmentLiveTrust.Validate(image, leaf, time.GetUtcNow()))
        {
            throw new InvalidDataException("ManagedCredentialInvalid");
        }

        AcceptedEnrollmentIssuer issuer = image.OnboardingContracts!.Issuers!.Values.Single(item =>
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

        X509Certificate2 anchor = new(root);
        try
        {
            if (pending is not null)
            {
                tls.PublishConfirmed(revisions, image, recoveryVersion, profile, credential.Id, 0, certificate, anchor,
                    issuer.Epoch, pendingBind: pending);
                return;
            }

            if (!revisions.TryAtCurrent(image, recoveryVersion, () => tls.Install(profile.RuntimeGeneration, certificate, anchor,
                image.Operations.Credentials.Values.Select(item => item.Fingerprint),
                candidate => EnrollmentLiveTrust.Validate(image, candidate, time.GetUtcNow()), issuer.Epoch)))
            {
                throw new InvalidDataException("ManagedReplayChanged");
            }
        }
        catch
        {
            anchor.Dispose();
            certificate.Dispose();
            throw;
        }
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
