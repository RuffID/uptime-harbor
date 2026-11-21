using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;

namespace UptimeHarbor.Infrastructure.Installation.Renewal;

/// <summary>Штатный проход renewal для опубликованного managed Bootstrap Direct credential.</summary>
internal class LocalVoterRenewal(InstallationLease lease, InstallationState installation,
    DynamicPeerTls tls, LocalVoterCredentialPreparation preparation, VoterCredentialRenewalAuthority authority,
    IRevisionTransport transport, VoterCredentialIssuanceRouter issuance, LocalVoterCredentialBindingCoordinator binding,
    LocalVoterCredentialInstallationCoordinator installed, LocalVoterCredentialPublication publication,
    IOptions<ClusterOptions> options, TimeProvider time, LocalVoterCredentialRetirement retirement,
    IVoterCredentialAdmissionClient? admission = null)
{
    internal async Task<DynamicPeerTls.Snapshot?> RunScheduledAsync(CancellationToken token)
    {
        await installation.VoterRenewal.WaitAsync(token);
        try
        {
            LocalInstallationProfile? profile = installation.Current;
            if (!installation.CommonOpen || options.Value.TlsBoundary != "Direct"
                || profile is not { Phase: InstallationPhase.ClusterReady, Mode: "Bootstrap" })
            {
                return null;
            }

            RevisionImage image = await authority.ReadFreshAsync(token);
            if (profile != RequireProfile(image) || !installation.CommonOpen)
            {
                return null;
            }

            using RuntimeGenerationContext guard = RuntimeGenerationContext.Extend(() =>
            {
                token.ThrowIfCancellationRequested();
                if (!installation.CommonOpen || installation.Current?.RuntimeGeneration != profile.RuntimeGeneration)
                {
                    throw new UnauthorizedAccessException("VoterCredentialWorkingRuntimeClosed");
                }
            });
            RuntimeGenerationContext.Verify();
            return await RunScheduledCoreAsync(image, profile, token);
        }
        finally
        {
            installation.VoterRenewal.Release();
        }
    }

    private async Task<DynamicPeerTls.Snapshot?> RunScheduledCoreAsync(RevisionImage image, LocalInstallationProfile profile,
        CancellationToken token)
    {
        Guid nodeId = options.Value.NodeId;
        VoterCredentialRotation? latest = VoterCredentialPolicy.Latest(image, nodeId);
        DynamicPeerTls.Snapshot? snapshot = tls.Capture();
        if (snapshot is { Quarantined: true })
        {
            return null;
        }

        if (latest is not null && (snapshot?.CredentialRevision ?? 0) < latest.CredentialRevision)
        {
            return await RenewCoreAsync(latest.Renewal.PreviousCredentialId, latest.Renewal.Issuance.IssuerEpoch, token);
        }

        if (latest is not null && snapshot is not null && snapshot.CredentialRevision == latest.CredentialRevision)
        {
            string reference = LocalVoterCredentialMaterial.Reference(lease.InstallIdentity, image.ClusterId, image.Generation,
                nodeId, options.Value.StorageGeneration, latest.Renewal.PreviousCredentialId);
            await retirement.ReconcileAsync(reference, latest, snapshot, image, token);
        }

        Guid? currentId = latest?.Renewal.Issuance.IssueId ?? profile.CredentialId;
        DateTimeOffset now = time.GetUtcNow();
        if (snapshot is null || currentId is not Guid credentialId || snapshot.CredentialId != credentialId
            || snapshot.CredentialRevision != (latest?.CredentialRevision ?? 0) || latest is { RetiredIndex: null }
            || !image.Operations.Credentials.TryGetValue(credentialId, out OperationalCredential? credential)
            || snapshot.Fingerprint != credential.Fingerprint || !VoterCredentialPolicy.IsUsable(image, credential, now)
            || credential.ExpiresAtUtc - now > TimeSpan.FromDays(7))
        {
            return null;
        }

        VoterCredentialRotation? pending = image.OnboardingContracts?.VoterCredentialRotations?.Values
            .SingleOrDefault(item => item.Renewal.Issuance.NodeId == nodeId
                && item.Renewal.PreviousCredentialId == credentialId);
        Guid? issuerEpoch = pending?.Renewal.Issuance.IssuerEpoch;
        if (!issuerEpoch.HasValue)
        {
            issuerEpoch = image.OnboardingContracts?.Issuers?.Values
                .SingleOrDefault(issuer => issuer.Purpose == EnrollmentIssuer.PEER_PURPOSE
                    && issuer.CertificateDigest == profile.TrustAnchorFingerprint && !issuer.RetiredAtUtc.HasValue
                    && !issuer.RevokedAtUtc.HasValue && now >= issuer.NotBeforeUtc && now < issuer.NotAfterUtc)?.Epoch;
        }

        if (!issuerEpoch.HasValue)
        {
            return null;
        }

        return await RenewCoreAsync(credentialId, issuerEpoch.Value, token);
    }

    public async Task<DynamicPeerTls.Snapshot> RenewAsync(Guid previousCredentialId, Guid issuerEpoch, CancellationToken token)
    {
        await installation.VoterRenewal.WaitAsync(token);
        try
        {
            return await RenewCoreAsync(previousCredentialId, issuerEpoch, token);
        }
        finally
        {
            installation.VoterRenewal.Release();
        }
    }

    private async Task<DynamicPeerTls.Snapshot> RenewCoreAsync(Guid previousCredentialId, Guid issuerEpoch,
        CancellationToken token)
    {
        RevisionImage image = await authority.ReadFreshAsync(token);
        LocalInstallationProfile profile = RequireProfile(image);
        AcceptedEnrollmentIssuer issuer = EnrollmentPeerAuthority.RequireIssuer(image, issuerEpoch, time.GetUtcNow());
        if (issuer.CertificateDigest != profile.TrustAnchorFingerprint)
        {
            throw new UnauthorizedAccessException("VoterCredentialPublicationIssuerChanged");
        }

        Guid node = options.Value.NodeId;
        Guid storage = options.Value.StorageGeneration;
        VoterCredentialRotation? accepted = image.OnboardingContracts?.VoterCredentialRotations?.Values
            .SingleOrDefault(item => item.Renewal.Issuance.NodeId == node
                && item.Renewal.PreviousCredentialId == previousCredentialId);
        string reference = LocalVoterCredentialMaterial.Reference(lease.InstallIdentity, image.ClusterId, image.Generation,
            node, storage, previousCredentialId);
        LocalVoterCredentialState? local = null;
        if (accepted is not null)
        {
            local = await preparation.ReadAsync(reference, token);
            if (local.Intent.Request.OperationId != accepted.AuthorizationOperationId
                || local.Intent.Request.Renewal != accepted.Renewal
                || local.Intent.Request.Renewal.Issuance.IssuerEpoch != issuerEpoch)
            {
                throw new InvalidDataException("VoterCredentialRenewalIntentChanged");
            }

            if (accepted.CredentialRevision > 0)
            {
                await binding.RecoverAcceptedAsync(reference, token);
                return await InstallAndPublishAsync(reference, token);
            }
        }

        VoterCredentialRotation? latest = VoterCredentialPolicy.Latest(image, node);
        DynamicPeerTls.Snapshot snapshot = tls.Capture()
            ?? throw new UnauthorizedAccessException("VoterCredentialPublicationUnavailable");
        OperationalCredential? credential = image.Operations.Credentials.GetValueOrDefault(previousCredentialId);
        if (snapshot.Quarantined || snapshot.CredentialId != previousCredentialId || credential is null
            || snapshot.Fingerprint != credential.Fingerprint
            || previousCredentialId != (latest?.Renewal.Issuance.IssueId ?? profile.CredentialId)
            || snapshot.CredentialRevision != (latest?.CredentialRevision ?? 0)
            || latest is { RetiredIndex: null })
        {
            throw new UnauthorizedAccessException("VoterCredentialPublishedRevisionChanged");
        }

        Guid keyOwner;
        string keyReference;
        if (latest is null)
        {
            keyOwner = profile.AttemptId ?? throw new InvalidDataException("VoterCredentialLocalKeyOwnerMissing");
            keyReference = profile.KeyReference ?? throw new InvalidDataException("VoterCredentialLocalKeyReferenceMissing");
        }
        else
        {
            keyOwner = previousCredentialId;
            keyReference = LocalVoterCredentialMaterial.Reference(lease.InstallIdentity, image.ClusterId, image.Generation,
                node, storage, latest.Renewal.PreviousCredentialId);
            LocalVoterCredentialInstallation previousInstallation = await installed.ReadInstalledAsync(keyReference, token);
            if (previousInstallation.CredentialRevision != snapshot.CredentialRevision
                || previousInstallation.IssueId != snapshot.CredentialId
                || previousInstallation.CertificateFingerprint != snapshot.Fingerprint)
            {
                throw new UnauthorizedAccessException("VoterCredentialInstalledRevisionChanged");
            }
        }

        VoterCredentialProof proof = CreateProof(image, profile);
        local ??= await preparation.PrepareAsync(image, node, previousCredentialId, issuerEpoch, keyOwner, keyReference,
            time.GetUtcNow(), token);
        VoterCredentialRequest request = local.Intent.Request;
        if (request.Renewal.PreviousCredentialRevision != snapshot.CredentialRevision
            || request.Renewal.PreviousFingerprint != snapshot.Fingerprint
            || local.Intent.PreviousKeyOwner != keyOwner || local.Intent.PreviousKeyReference != keyReference
            || !ReferenceEquals(snapshot, tls.Capture()) || profile != installation.Current)
        {
            throw new UnauthorizedAccessException("VoterCredentialPublishedRevisionChanged");
        }

        VoterCredentialProofRequest call = await ProveAsync(local, proof, token);
        VoterCredentialSigningRequest? signing = await AuthorizeAsync(call, local.Authorization is not null, token);
        if (signing is null)
        {
            local = await preparation.ReplaceRejectedAsync(reference, call, token);
            image = await authority.ReadFreshAsync(token);
            if (!ReferenceEquals(snapshot, tls.Capture()) || profile != RequireProfile(image))
            {
                throw new UnauthorizedAccessException("VoterCredentialPublishedRevisionChanged");
            }

            call = await ProveAsync(local, CreateProof(image, profile), token);
            signing = await AuthorizeAsync(call, authorizationPinned: false, token)
                ?? throw new UnauthorizedAccessException("VoterCredentialReplacementRejected");
        }

        if (local.Authorization is null)
        {
            local = await preparation.PinAuthorizationAsync(reference, signing, token);
        }

        if (local.Artifact is null)
        {
            RuntimeGenerationContext.Verify();
            EnrollmentIssuedArtifact artifact = await issuance.IssueAsync(signing, token);
            await preparation.PinArtifactAsync(reference, signing, artifact, token);
        }

        await binding.BindAsync(reference, signing, admission, token);
        return await InstallAndPublishAsync(reference, token);
    }

    private async Task<VoterCredentialProofRequest> ProveAsync(LocalVoterCredentialState local, VoterCredentialProof proof,
        CancellationToken token)
    {
        VoterCredentialRequest request = local.Intent.Request;
        EnrollmentIssuance template = request.Renewal.Issuance;
        string nonce;
        if (admission is not null)
        {
            nonce = await admission.IssueNonceAsync(new(request, proof), token);
        }
        else
        {
            nonce = await authority.IssueNonceAsync(request, proof, template.NodeId, template.StorageGeneration, token);
        }

        return await preparation.ProveAsync(local, proof with { Nonce = nonce }, token);
    }

    private async Task<VoterCredentialSigningRequest?> AuthorizeAsync(VoterCredentialProofRequest call, bool authorizationPinned,
        CancellationToken token)
    {
        VoterCredentialRequest request = call.Request;
        EnrollmentIssuance template = request.Renewal.Issuance;
        RevisionImage image = await authority.ReadBindingAsync(request, token);
        VoterCredentialRotation? rotation = image.OnboardingContracts?.VoterCredentialRotations?.GetValueOrDefault(template.IssueId);
        CommandResult? result = image.Results.GetValueOrDefault(request.OperationId);
        if (rotation is null)
        {
            if (authorizationPinned)
            {
                throw new InvalidDataException("VoterCredentialAuthorizeResultConflicts");
            }

            if (result is null)
            {
                CommandResult response = await SubmitAuthorizeAsync(call, token);
                image = await authority.ReadBindingAsync(request, token);
                rotation = image.OnboardingContracts?.VoterCredentialRotations?.GetValueOrDefault(template.IssueId);
                result = image.Results.GetValueOrDefault(request.OperationId);
                if (result is null || response != result)
                {
                    throw new InvalidDataException("VoterCredentialAuthorizeResponseChanged");
                }
            }

            if (rotation is null && result.Outcome == OperationOutcome.Rejected)
            {
                // The replacement writer rechecks the exact rejection under a fresh barrier.
                return null;
            }
        }

        VoterCredentialSigningRequest signing = new(1, call,
            rotation?.AuthorizedIndex ?? throw new UnauthorizedAccessException("VoterCredentialExactAuthorizationRequired"));
        await authority.ConfirmSigningAsync(signing, template.NodeId, template.StorageGeneration, token);
        return signing;
    }

    private async Task<CommandResult> SubmitAuthorizeAsync(VoterCredentialProofRequest call, CancellationToken token)
    {
        RuntimeGenerationContext.Verify();
        if (admission is not null)
        {
            return await admission.AuthorizeAsync(new(VoterCredentialAuthorizationCommand.Create(call.Request), call), token);
        }

        EnrollmentIssuance template = call.Request.Renewal.Issuance;
        RevisionCommand command = await authority.PrepareAuthorizationReplayAsync(call, template.NodeId,
            template.StorageGeneration, token);
        RuntimeGenerationContext.Verify();
        IReadOnlyList<CommandResult> response = await transport.SendAsync([command], token);
        if (response.Count != 1)
        {
            throw new InvalidDataException("VoterCredentialAuthorizeResponseChanged");
        }

        return response[0];
    }

    private LocalInstallationProfile RequireProfile(RevisionImage image)
    {
        LocalInstallationProfile? profile = installation.Current;
        if (options.Value.TlsBoundary != "Direct" || profile is not { Phase: InstallationPhase.ClusterReady, Mode: "Bootstrap" }
            || profile.InstallIdentity != lease.InstallIdentity || profile.BootId != lease.BootId
            || profile.ClusterId != image.ClusterId || profile.ClusterGeneration != image.Generation
            || profile.NodeId != options.Value.NodeId || profile.StorageGeneration != options.Value.StorageGeneration
            || image.Operations.Nodes.GetValueOrDefault(options.Value.NodeId)?.Address != profile.PeerAddress)
        {
            throw new UnauthorizedAccessException("VoterCredentialRenewalTopologyUnavailable");
        }

        return profile;
    }

    private VoterCredentialProof CreateProof(RevisionImage image, LocalInstallationProfile profile)
    {
        OnlineProcessLease? process = image.OnboardingContracts?.OnlineProcesses?.GetValueOrDefault(options.Value.NodeId);
        DateTimeOffset now = time.GetUtcNow();
        if (process is null || process.BootId != profile.BootId || process.BootChallenge != lease.BootChallenge
            || now >= process.LeaseUntilUtc)
        {
            throw new UnauthorizedAccessException("VoterCredentialProcessNotCurrent");
        }

        return new(1, VoterCredentialProofs.SCOPE, options.Value.NodeId, options.Value.StorageGeneration,
            process.BootId, process.BootChallenge, process.ProcessEpoch, process.RecoveryVersion,
            image.Operations.MembershipVersion, string.Empty, now, string.Empty);
    }

    private async Task<DynamicPeerTls.Snapshot> InstallAndPublishAsync(string reference, CancellationToken token)
    {
        await installed.InstallAsync(reference, token);
        return await publication.PublishAsync(reference, token);
    }
}
