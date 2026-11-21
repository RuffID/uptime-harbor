using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;

namespace UptimeHarbor.Infrastructure.Installation.Renewal;

/// <summary>Публикация подтверждённого B2b2 материала. Для рестарта разделяет native replay и majority.</summary>
public sealed class LocalVoterCredentialPublication(LocalVoterCredentialInstallationCoordinator installed,
    LocalVoterCredentialBindingCoordinator binding, LocalVoterCredentialFiles files, InstallationLease lease, InstallationState installation,
    RevisionStateStore revisions, DynamicPeerTls tls)
{
    public async Task<DynamicPeerTls.Snapshot> PublishAsync(string reference, CancellationToken token)
    {
        await installation.Transition.WaitAsync(token);
        try
        {
            LocalInstallationProfile profile = installation.Current
                ?? throw new InvalidOperationException("PeerCredentialProfileMissing");
            long recoveryVersion = revisions.RecoveryVersion;
            var evidence = await installed.VerifyAsync(reference, token);
            token.ThrowIfCancellationRequested();
            return PublishVerified(profile, evidence, recoveryVersion);
        }
        catch
        {
            tls.BlockPublication();
            throw;
        }
        finally
        {
            installation.Transition.Release();
        }
    }

    internal void PrepareQuarantine(LocalInstallationProfile profile, RevisionImage image, VoterCredentialRotation rotation,
        long recoveryVersion)
    {
        var pending = ReadPendingReplay(profile, image, rotation.Renewal.Issuance.IssueId, rotation.CredentialRevision);
        var evidence = installed.VerifyReplayed(Reference(profile, image, rotation.Renewal.PreviousCredentialId), image);
        if (pending is not null)
        {
            // A later intent can only have been written using an already published predecessor.
            PublishVerified(profile, evidence, recoveryVersion, restart: true, pendingBind: pending);
            return;
        }

        LocalVoterCredentialReplay replay = new(evidence.Binding, new(image.ClusterId, image.Generation, image.Index, image.Term, image.Digest));
        PublishVerified(profile, evidence, recoveryVersion, restart: true, replay: replay);
    }

    internal LocalVoterPendingBindReplay? ReadPendingReplay(LocalInstallationProfile profile, RevisionImage image,
        Guid credentialId, long credentialRevision)
    {
        var pending = binding.ReadPendingReplay(Reference(profile, image, credentialId), image, credentialId, credentialRevision);
        if (pending is null)
        {
            return null;
        }

        var predecessor = VoterCredentialPolicy.Latest(image, profile.NodeId!.Value);
        Guid? owner = predecessor is null ? profile.AttemptId : predecessor.Renewal.Issuance.IssueId;
        string? reference = predecessor is null ? profile.KeyReference
            : Reference(profile, image, predecessor.Renewal.PreviousCredentialId);
        if (pending.Material.PreviousKeyOwner != owner || pending.Material.PreviousKeyReference != reference)
        {
            throw new InvalidDataException("VoterCredentialLocalPreviousIntentChanged");
        }

        return pending;
    }

    public async Task<bool> ConfirmRestartAsync(CancellationToken token)
    {
        await installation.Transition.WaitAsync(token);
        try
        {
            LocalInstallationProfile profile = installation.Current
                ?? throw new InvalidOperationException("PeerCredentialProfileMissing");
            RevisionImage image = revisions.Current ?? throw new InvalidOperationException("ProjectionUnavailable");
            if (!profile.ManagedRestart || profile.NodeId is not Guid node)
            {
                throw new InvalidOperationException("PeerCredentialRestartProfileChanged");
            }

            var rotation = VoterCredentialPolicy.Latest(image, node);
            var snapshot = tls.Capture();
            if (snapshot?.PendingBind is { } pending)
            {
                return await ConfirmPendingAsync(profile, snapshot, pending, token);
            }

            if (rotation is null)
            {
                return snapshot is { CredentialRevision: 0 } && snapshot.CredentialId == profile.CredentialId;
            }

            if (snapshot is null || snapshot.CredentialId != rotation.Renewal.Issuance.IssueId
                || snapshot.CredentialRevision != rotation.CredentialRevision)
            {
                return false;
            }

            if (snapshot.RestartReplay is not { } replay)
            {
                return true;
            }

            long recoveryVersion = revisions.RecoveryVersion;
            RequireNoPendingReplay(profile, image, rotation.Renewal.Issuance.IssueId, rotation.CredentialRevision);
            string reference = Reference(profile, image, rotation.Renewal.PreviousCredentialId);
            var evidence = await installed.CompleteRestartAsync(reference, replay, token);
            token.ThrowIfCancellationRequested();
            if (evidence.Manifest.IssueId != snapshot.CredentialId
                || evidence.Manifest.CredentialRevision != snapshot.CredentialRevision)
            {
                throw new InvalidDataException("PeerCredentialRestartSlotChanged");
            }

            RequireNoPendingReplay(profile, evidence.Image, evidence.Manifest.IssueId, evidence.Manifest.CredentialRevision);
            PublishVerified(profile, evidence, recoveryVersion, restart: true);
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or UnauthorizedAccessException or CryptographicException or FormatException)
        {
            tls.BlockPublication();
            throw;
        }
        finally
        {
            installation.Transition.Release();
        }
    }

    private async Task<bool> ConfirmPendingAsync(LocalInstallationProfile profile, DynamicPeerTls.Snapshot snapshot,
        LocalVoterPendingBindReplay pending, CancellationToken token)
    {
        long recoveryVersion = revisions.RecoveryVersion;
        var (image, accepted) = await binding.ConfirmPendingReplayAsync(pending, token);
        token.ThrowIfCancellationRequested();
        RuntimeGenerationContext.Verify();
        if (!ReferenceEquals(snapshot, tls.Capture()) || profile != installation.Current)
        {
            return false;
        }

        if (accepted is not null)
        {
            var replay = new LocalVoterCredentialReplay(accepted,
                new(image.ClusterId, image.Generation, image.Index, image.Term, image.Digest), pending);
            using RuntimeGenerationContext guard = RuntimeGenerationContext.Extend(() =>
            {
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(snapshot, tls.Capture()) || profile != installation.Current)
                {
                    throw new UnauthorizedAccessException("VoterCredentialPendingRestartClosed");
                }
            });
            var evidence = await installed.CompleteRestartAsync(pending.Material.KeyReference, replay, token);
            token.ThrowIfCancellationRequested();
            RequireNoPendingReplay(profile, evidence.Image, evidence.Manifest.IssueId, evidence.Manifest.CredentialRevision);
            PublishVerified(profile, evidence, recoveryVersion, restart: true);
            return true;
        }

        if (snapshot.CredentialRevision > 0)
        {
            var predecessor = VoterCredentialPolicy.Latest(image, profile.NodeId!.Value)!;
            var evidence = installed.VerifyReplayed(Reference(profile, image, predecessor.Renewal.PreviousCredentialId), image);
            LocalVoterCredentialInstallationCoordinator.RequireConfirmed(evidence);
        }

        // Keep correlation through Process/Enable: Raft may apply the original Bind after this cut.
        tls.PublishConfirmed(revisions, image, recoveryVersion, profile, snapshot.CredentialId!.Value,
            snapshot.CredentialRevision, snapshot.Certificate, snapshot.Root, snapshot.IssuerEpoch!.Value,
            pendingBind: pending, pendingConfirmed: true);
        return true;
    }

    private void RequireNoPendingReplay(LocalInstallationProfile profile, RevisionImage image, Guid credentialId, long revision)
    {
        if (ReadPendingReplay(profile, image, credentialId, revision) is not null)
        {
            throw new InvalidDataException("VoterCredentialInstallationAheadOfReplay");
        }
    }

    private string Reference(LocalInstallationProfile profile, RevisionImage image, Guid previousCredentialId)
    {
        return LocalVoterCredentialMaterial.Reference(lease.InstallIdentity, image.ClusterId, image.Generation,
            profile.NodeId!.Value, profile.StorageGeneration!.Value, previousCredentialId);
    }

    private DynamicPeerTls.Snapshot PublishVerified(LocalInstallationProfile profile,
        LocalVoterCredentialInstallationCoordinator.VerifiedInstallation evidence, long recoveryVersion,
        bool restart = false, LocalVoterCredentialReplay? replay = null, LocalVoterPendingBindReplay? pendingBind = null)
    {
        if (replay is null)
        {
            LocalVoterCredentialInstallationCoordinator.RequireConfirmed(evidence);
        }

        EnrollmentIssuance template = evidence.Local.Intent.Request.Renewal.Issuance;
        AcceptedEnrollmentIssuer issuer = evidence.Local.Authorization!.Issuer;
        if (profile.InstallIdentity != lease.InstallIdentity || profile.BootId != lease.BootId
            || profile.NodeId != template.NodeId || profile.StorageGeneration != template.StorageGeneration
            || profile.ClusterId != evidence.Image.ClusterId || profile.ClusterGeneration != evidence.Image.Generation
            || profile.PeerAddress != template.PeerUri || !restart && profile.TrustAnchorFingerprint != issuer.CertificateDigest)
        {
            throw new InvalidDataException("PeerCredentialPublicationProfileChanged");
        }

        return PublishMaterial(profile, evidence, issuer, recoveryVersion, replay, pendingBind);
    }

    private DynamicPeerTls.Snapshot PublishMaterial(LocalInstallationProfile profile,
        LocalVoterCredentialInstallationCoordinator.VerifiedInstallation evidence,
        AcceptedEnrollmentIssuer issuer, long recoveryVersion, LocalVoterCredentialReplay? replay,
        LocalVoterPendingBindReplay? pendingBind)
    {
        LocalVoterCredentialManifest manifest = evidence.Manifest;
        (LocalVoterCredentialIntent intent, ECDsa key) = LocalVoterCredentialRetry.ReadIntent(
            files, lease.InstallIdentity, manifest.MaterialReference)
            ?? throw new InvalidDataException("PeerCredentialPublicationIntentMissing");
        using ECDsa protectedKey = key;
        if (intent != evidence.Local.Intent)
        {
            throw new InvalidDataException("PeerCredentialPublicationIntentChanged");
        }

        return PublishMaterial(tls, revisions, profile, evidence.Image, recoveryVersion, manifest.IssueId,
            manifest.CredentialRevision, evidence.Local.Artifact!.CertificateDer, protectedKey, issuer, replay, pendingBind);
    }

    internal static DynamicPeerTls.Snapshot PublishMaterial(DynamicPeerTls tls, RevisionStateStore revisions,
        LocalInstallationProfile profile, RevisionImage image, long recoveryVersion, Guid issueId, long revision,
        byte[] der, ECDsa key, AcceptedEnrollmentIssuer issuer, LocalVoterCredentialReplay? replay = null,
        LocalVoterPendingBindReplay? pendingBind = null, LocalVoterContinuationQuarantine? continuation = null)
    {
        using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(der);
        X509Certificate2 certificate = leaf.CopyWithPrivateKey(key);
        X509Certificate2? root = null;
        try
        {
            root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(issuer.CertificateDer));
            DynamicPeerTls.Snapshot snapshot = tls.PublishConfirmed(revisions, image, recoveryVersion,
                profile, issueId, revision, certificate, root, issuer.Epoch, replay, pendingBind, continuation: continuation);
            if (ReferenceEquals(snapshot.Certificate, certificate))
            {
                root = null;
                return snapshot;
            }

            certificate.Dispose();
            return snapshot;
        }
        catch
        {
            certificate.Dispose();
            throw;
        }
        finally
        {
            root?.Dispose();
        }
    }
}
