using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;

namespace UptimeHarbor.Web.Cluster.EnrollmentRouting;

/// <summary>Владеет повторной accepted авторизацией typed peer requests независимо от контроллера.</summary>
public class EnrollmentPeerService(EnrollmentCommandReceiver receiver, EnrollmentSigner signer, OnlineGrantSigner grants,
    RevisionStateStore revisions, IEnrollmentReadBarrier barrier, IOptions<ClusterOptions> options, TimeProvider time)
{
    public async Task<CommandResult> CoordinateAsync(EnrollmentCoordinatedRequest request, PeerEnvelope peer, CancellationToken token)
    {
        await RequirePeerAsync(peer, token);
        return await receiver.AcceptAsync(request, peer.FromNodeId, peer.StorageGeneration, token);
    }

    public async Task<EnrollmentIssuedArtifact> IssueAsync(EnrollmentSigningRequest request, PeerEnvelope peer, CancellationToken token)
    {
        RevisionImage image = await RequirePeerAsync(peer, token);
        RequireOwner(image, request.Issuance.IssuerEpoch);
        OnboardingAttempt? attempt = image.Onboarding?.Invitations.GetValueOrDefault(request.Request.InvitationId)?.Attempt;
        if (request.Version != 1 || attempt is null || attempt.Request != request.Request || attempt.Issuance != request.Issuance
            || request.AuthorizationIndex != attempt.IssuanceAuthorizationIndex || request.CsrDer.Length is < 1 or > 87384)
        {
            throw new UnauthorizedAccessException("EnrollmentPeerTemplateInvalid");
        }

        byte[] csr = Convert.FromBase64String(request.CsrDer);
        try
        {
            EnrollmentIssuedArtifact artifact = await signer.IssueAsync(request.Request, request.Issuance, csr, token);
            image = await RequirePeerAsync(peer, token);
            RequireOwner(image, request.Issuance.IssuerEpoch);
            EnrollmentArtifactValidation.Require(image, request.Request, request.Issuance, artifact, time.GetUtcNow());
            return artifact;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(csr);
        }
    }

    public async Task<OnlineGrant> GrantAsync(OnlineGrantSigningRequest request, PeerEnvelope peer, CancellationToken token)
    {
        RevisionImage image = await RequirePeerAsync(peer, token);
        RequireOwner(image, request.Grant.IssuerEpoch);
        if (request.Grant.NodeId != peer.FromNodeId || request.Grant.StorageGeneration != peer.StorageGeneration)
        {
            throw new UnauthorizedAccessException("OnlineGrantPeerSourceInvalid");
        }

        OnlineGrant signed = await grants.SignRequestAsync(request, token);
        image = await RequirePeerAsync(peer, token);
        RequireOwner(image, request.Grant.IssuerEpoch);
        if (!OnlineGrantPolicy.Current(image, signed, time.GetUtcNow(), signed.Purpose == OnlineGrantPurpose.Process)
            || !OnlineGrantSignatures.Verify(image, signed, time.GetUtcNow()))
        {
            throw new UnauthorizedAccessException("OnlineGrantPeerAuthorityChanged");
        }

        return signed;
    }

    private async Task<RevisionImage> RequirePeerAsync(PeerEnvelope peer, CancellationToken token)
    {
        await barrier.ConfirmAsync(token);
        RevisionImage image = revisions.Current ?? throw new InvalidOperationException("EnrollmentPeerProjectionUnavailable");
        if (peer.ClusterId != image.ClusterId || peer.Generation != image.Generation || peer.ToNodeId != options.Value.NodeId
            || peer.Compatibility != OnboardingProtocol.TRUST_COMPATIBILITY)
        {
            throw new UnauthorizedAccessException("EnrollmentPeerOriginInvalid");
        }

        EnrollmentPeerAuthority.RequireVoter(image, peer.FromNodeId, peer.StorageGeneration);
        EnrollmentPeerAuthority.RequireVoter(image, options.Value.NodeId, options.Value.StorageGeneration);
        return image;
    }

    private void RequireOwner(RevisionImage image, Guid epoch)
    {
        AcceptedEnrollmentIssuer issuer = EnrollmentPeerAuthority.RequireIssuer(image, epoch, time.GetUtcNow());
        if (issuer.NodeId != options.Value.NodeId || issuer.StorageGeneration != options.Value.StorageGeneration)
        {
            throw new UnauthorizedAccessException("EnrollmentPeerSignerNotOwner");
        }
    }
}
