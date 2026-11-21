using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

/// <summary>Production ManagedRestart использует только read-state для сверки Bind.</summary>
public class VoterCredentialRenewalAuthority(RevisionStateStore revisions, IEnrollmentReadBarrier barrier,
    EnrollmentNonces nonces, IOptions<ClusterOptions> options, TimeProvider time) : IVoterCredentialRenewalAuthority
{
    private readonly VoterCredentialLegacyTrust? legacyTrust;

    internal VoterCredentialRenewalAuthority(RevisionStateStore revisions, IEnrollmentReadBarrier barrier,
        EnrollmentNonces nonces, IOptions<ClusterOptions> options, TimeProvider time, VoterCredentialLegacyTrust legacyTrust)
        : this(revisions, barrier, nonces, options, time)
    {
        this.legacyTrust = legacyTrust;
    }

    public VoterCredentialLegacyTrust LegacyTrust => legacyTrust ?? new(options.Value);

    public bool IsCurrent(RevisionImage image)
    {
        return revisions.TryAtCurrent(image, revisions.RecoveryVersion, static () => { });
    }

    public async ValueTask<string> IssueNonceAsync(VoterCredentialRequest request, VoterCredentialProof proof,
        Guid authenticatedNodeId, Guid authenticatedStorage, CancellationToken token)
    {
        RevisionImage image = await ReadFreshAsync(token);
        RequireSource(request, authenticatedNodeId, authenticatedStorage);
        VoterCredentialAuthorityPolicy.RequireCurrent(image, request, time.GetUtcNow());
        return nonces.Issue(request.Renewal.Issuance.IssueId, proof.BootId, proof.BootChallenge, proof.ProcessEpoch,
            VoterCredentialProofs.SCOPE, VoterCredentialProofs.Digest(request));
    }

    public async ValueTask<RevisionCommand> PrepareAuthorizationAsync(VoterCredentialProofRequest call,
        Guid authenticatedNodeId, Guid authenticatedStorage, CancellationToken token)
    {
        return await PrepareAuthorizationCoreAsync(call, authenticatedNodeId, authenticatedStorage, false, token);
    }

    // Только для exact replay в изолированной композиции. Окно проверит reducer на позиции принятия.
    internal async ValueTask<RevisionCommand> PrepareAuthorizationReplayAsync(VoterCredentialProofRequest call,
        Guid authenticatedNodeId, Guid authenticatedStorage, CancellationToken token)
    {
        return await PrepareAuthorizationCoreAsync(call, authenticatedNodeId, authenticatedStorage, true, token);
    }

    private async ValueTask<RevisionCommand> PrepareAuthorizationCoreAsync(VoterCredentialProofRequest call,
        Guid authenticatedNodeId, Guid authenticatedStorage, bool allowExpiredReplay, CancellationToken token)
    {
        long recovery = revisions.RecoveryVersion;
        RevisionImage image = await ReadFreshAsync(token);
        RequireSource(call.Request, authenticatedNodeId, authenticatedStorage);
        VoterCredentialProofs.Require(image, call, time.GetUtcNow(), LegacyTrust);
        EnrollmentProof nonceProof = new(1, call.Request.OperationId, VoterCredentialProofs.SCOPE,
            call.Proof.BootId, call.Proof.BootChallenge, call.Proof.ProcessEpoch, call.Proof.Nonce,
            VoterCredentialProofs.Digest(call.Request), string.Empty);
        VoterCredentialRequest request = call.Request;
        EnrollmentIssuance leaf = request.Renewal.Issuance;
        RequireUnchanged(image, recovery, token);
        if (!nonces.Consume(leaf.IssueId, nonceProof))
        {
            throw new UnauthorizedAccessException("VoterCredentialNonceInvalid");
        }

        VoterCredentialRotation? existing = image.OnboardingContracts?.VoterCredentialRotations?.GetValueOrDefault(leaf.IssueId);
        DateTimeOffset intentAt = leaf.NotAfterUtc.AddDays(-30);
        DateTimeOffset now = time.GetUtcNow();
        if (existing is null && (intentAt > now || !allowExpiredReplay && now - intentAt > TimeSpan.FromSeconds(10)))
        {
            throw new UnauthorizedAccessException("VoterCredentialIntentNotFresh");
        }

        RevisionCommand command = VoterCredentialAuthorizationCommand.Create(request);
        if (existing is not null && (existing.Renewal != request.Renewal
            || existing.AuthorizationOperationId != request.OperationId || existing.AuthorizationDigest != RevisionCodec.Digest(command)))
        {
            throw new UnauthorizedAccessException("VoterCredentialIntentChanged");
        }

        RequireUnchanged(image, recovery, token);
        return command;
    }

    internal async ValueTask<RevisionImage> ConfirmBindingAsync(RevisionCommand command,
        VoterCredentialSigningRequest call, EnrollmentIssuedArtifact artifact, CancellationToken token)
    {
        if (options.Value.TlsBoundary != "Direct")
        {
            throw new UnauthorizedAccessException("VoterCredentialBindingDirectRequired");
        }

        long recovery = revisions.RecoveryVersion;
        RevisionImage image = await ReadBindingAsync(call.ProofRequest.Request, token);
        VoterCredentialArtifactValidation.Require(image, call, artifact, time.GetUtcNow(), LegacyTrust);
        if (command != VoterCredentialBindCommand.Create(call.ProofRequest.Request, artifact, command.OperationId))
        {
            throw new UnauthorizedAccessException("VoterCredentialBindCommandChanged");
        }

        RequireUnchanged(image, recovery, token);
        return image;
    }

    private void RequireUnchanged(RevisionImage image, long recovery, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RuntimeGenerationContext.Verify();
        if (!revisions.TryAtCurrent(image, recovery, static () => { }))
        {
            throw new UnauthorizedAccessException("VoterCredentialFreshProjectionRequired");
        }
    }

    public async ValueTask<RevisionImage> ConfirmSigningAsync(VoterCredentialSigningRequest call,
        Guid authenticatedNodeId, Guid authenticatedStorage, CancellationToken token)
    {
        RevisionImage image = await ReadFreshAsync(token);
        RequireSource(call.ProofRequest.Request, authenticatedNodeId, authenticatedStorage);
        VoterCredentialAuthorityPolicy.RequireAccepted(image, call, time.GetUtcNow(), LegacyTrust);
        return image;
    }

    /// <summary>Свежий локальный срез для Bind reconciliation; не разрешает подпись или dispatch.</summary>
    public async ValueTask<RevisionImage> ReadBindingAsync(VoterCredentialRequest request, CancellationToken token)
    {
        RevisionImage image = await ReadFreshAsync(token);
        RequireSource(request, options.Value.NodeId, options.Value.StorageGeneration);
        if (request.ClusterId != image.ClusterId || request.Generation != image.Generation)
        {
            throw new UnauthorizedAccessException("VoterCredentialBindingClusterChanged");
        }

        return image;
    }

    public async ValueTask<RevisionImage> ReadFreshAsync(CancellationToken token)
    {
        long recovery = revisions.RecoveryVersion;
        await barrier.ConfirmAsync(token);
        token.ThrowIfCancellationRequested();
        RevisionImage? image = revisions.Current;
        if (!revisions.Available || image is null || recovery != revisions.RecoveryVersion
            || options.Value.Compatibility != OnboardingProtocol.TRUST_COMPATIBILITY
            || options.Value.ClusterId != image.ClusterId || options.Value.Generation != image.Generation)
        {
            throw new UnauthorizedAccessException("VoterCredentialFreshProjectionRequired");
        }

        EnrollmentPeerAuthority.RequireVoter(image, options.Value.NodeId, options.Value.StorageGeneration);

        return image;
    }

    private static void RequireSource(VoterCredentialRequest request, Guid nodeId, Guid storage)
    {
        if (request.Renewal.Issuance.NodeId != nodeId || request.Renewal.Issuance.StorageGeneration != storage)
        {
            throw new UnauthorizedAccessException("VoterCredentialAuthenticatedOriginMismatch");
        }
    }
}
