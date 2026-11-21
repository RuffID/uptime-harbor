using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Installation.Renewal;

namespace UptimeHarbor.Web.Cluster.EnrollmentRouting;

/// <summary>Проверяет удостоверенный voter envelope и фактическую Direct TLS-пару.</summary>
public class VoterCredentialPeerService(VoterCredentialSigner signer, VoterCredentialRenewalAuthority authority,
    IClusterStateView state, IOptions<ClusterOptions> configured, TimeProvider time, DynamicPeerTls? tls = null)
{
    internal async Task<VoterCredentialHandoff> ConfirmHandoffAsync(VoterCredentialHandoff handoff, PeerEnvelope peer,
        PeerCredential presented, Guid localCredentialId, string localFingerprint, CancellationToken token)
    {
        await ConfirmHandoffCoreAsync(handoff, peer, presented, localCredentialId, localFingerprint, null, token);
        return handoff;
    }

    internal async Task<VoterCredentialRetirementEvidence> AttestRetirementAsync(VoterCredentialRetirementIntent intent,
        PeerEnvelope peer, PeerCredential presented, Guid localCredentialId, string localFingerprint,
        DynamicPeerTls.Snapshot? snapshot, X509Certificate2? legacyCertificate, CancellationToken token)
    {
        if (intent.Handoff is null)
        {
            throw new UnauthorizedAccessException("VoterCredentialRetirementIntentInvalid");
        }

        var (image, consensus) = await ConfirmHandoffCoreAsync(intent.Handoff, peer, presented, localCredentialId,
            localFingerprint, intent, token);
        ClusterOptions local = configured.Value;
        if (snapshot is not null && (!ReferenceEquals(tls?.Capture(), snapshot)
            || snapshot.CredentialId != localCredentialId || snapshot.Fingerprint != localFingerprint || snapshot.Quarantined))
        {
            throw new UnauthorizedAccessException("VoterCredentialRetirementWitnessPublicationChanged");
        }

        X509Certificate2 certificate = snapshot?.Certificate ?? legacyCertificate
            ?? throw new UnauthorizedAccessException("VoterCredentialRetirementWitnessKeyUnavailable");
        VoterCredentialRetirementEvidence unsigned = new(local.NodeId, local.StorageGeneration, localCredentialId,
            localFingerprint, consensus.Term, consensus.MembershipVersion, time.GetUtcNow(), string.Empty);
        VoterCredentialRetirementEvidence evidence = unsigned.Sign(intent, certificate);
        evidence.Require(intent, image, consensus, time.GetUtcNow());
        if (!authority.IsCurrent(image) || !ReferenceEquals(state.Consensus, consensus)
            || snapshot is not null && !ReferenceEquals(tls?.Capture(), snapshot))
        {
            throw new UnauthorizedAccessException("VoterCredentialHandoffEvidenceChanged");
        }

        token.ThrowIfCancellationRequested();
        RuntimeGenerationContext.Verify();
        return evidence;
    }

    private async Task<(RevisionImage Image, ConsensusStatus Consensus)> ConfirmHandoffCoreAsync(VoterCredentialHandoff handoff,
        PeerEnvelope peer, PeerCredential presented, Guid localCredentialId, string localFingerprint,
        VoterCredentialRetirementIntent? intent, CancellationToken token)
    {
        ClusterOptions local = configured.Value;
        if (local.TlsBoundary != "Direct" || peer.ToNodeId != local.NodeId || peer.FromNodeId == local.NodeId
            || peer.Compatibility != OnboardingProtocol.TRUST_COMPATIBILITY || peer.Term <= 0
            || peer.CredentialId != presented.CredentialId || presented.CredentialId != handoff.IssueId
            || presented.Fingerprint != handoff.Fingerprint || presented.NodeId != peer.FromNodeId
            || presented.StorageGeneration != peer.StorageGeneration || !presented.IsVoting || presented.IsAdmission)
        {
            throw new UnauthorizedAccessException("VoterCredentialHandoffOriginMismatch");
        }

        RevisionImage image = await authority.ReadFreshAsync(token);
        if (peer.ClusterId != image.ClusterId || peer.Generation != image.Generation
            || state.Consensus is not { } consensus || consensus.NodeId != local.NodeId || consensus.Term != peer.Term
            || consensus.ClusterId != image.ClusterId || consensus.Generation != image.Generation
            || !consensus.VotingNodes.Contains(peer.FromNodeId) || !consensus.VotingNodes.Contains(local.NodeId)
            || consensus.MembershipVersion != image.Operations.MembershipVersion || consensus.ConsensusToken.IsCancellationRequested)
        {
            throw new UnauthorizedAccessException("VoterCredentialHandoffEvidenceChanged");
        }

        DateTimeOffset now = time.GetUtcNow();
        if (intent is null)
        {
            handoff.RequireSubject(image, peer.FromNodeId, peer.StorageGeneration, now);
        }
        else
        {
            EnrollmentIssuance subject = intent.Require(image, now).Renewal.Issuance;
            if (subject.NodeId != peer.FromNodeId || subject.StorageGeneration != peer.StorageGeneration)
            {
                throw new UnauthorizedAccessException("VoterCredentialHandoffOriginMismatch");
            }
        }

        OperationalNode witness = EnrollmentPeerAuthority.RequireVoter(image, local.NodeId, local.StorageGeneration);
        VoterCredentialHandoff.RequireLeaf(image, witness, localCredentialId, localFingerprint, now);
        if (presented.Address != new Uri(image.Operations.Nodes[peer.FromNodeId].Address) || presented.ExpiresAtUtc <= now
            || witness.Address != local.PublicUri || !authority.IsCurrent(image) || consensus.ConsensusToken.IsCancellationRequested)
        {
            throw new UnauthorizedAccessException("VoterCredentialHandoffEvidenceChanged");
        }

        RuntimeGenerationContext.Verify();
        return (image, consensus);
    }

    public async Task<EnrollmentIssuedArtifact> IssueAsync(VoterCredentialSigningRequest call, PeerEnvelope peer, PeerCredential presented,
        CancellationToken token, Guid localCredentialId, string localFingerprint)
    {
        var before = await ConfirmAsync(call, peer, presented, localCredentialId, localFingerprint, token);
        EnrollmentIssuedArtifact artifact = await signer.IssueAsync(call, peer.FromNodeId, peer.StorageGeneration, token);
        var after = await ConfirmAsync(call, peer, presented, localCredentialId, localFingerprint, token);
        if (before.Evidence != after.Evidence)
        {
            throw new UnauthorizedAccessException("VoterCredentialPeerAuthorityChanged");
        }

        VoterCredentialArtifactValidation.Require(after.Image, call, artifact, time.GetUtcNow(), authority.LegacyTrust);
        return artifact;
    }

    private async Task<(RevisionImage Image, VoterCredentialPeerEvidence Evidence)> ConfirmAsync(
        VoterCredentialSigningRequest call, PeerEnvelope peer, PeerCredential presented, Guid issuerCredentialId,
        string issuerFingerprint, CancellationToken token)
    {
        ClusterOptions local = configured.Value;
        VoterCredentialRequest request = call.ProofRequest.Request;
        if (peer.ClusterId != request.ClusterId || peer.Generation != request.Generation
            || peer.ToNodeId != local.NodeId || peer.FromNodeId == local.NodeId
            || peer.Compatibility != OnboardingProtocol.TRUST_COMPATIBILITY
            || peer.CredentialId != request.Renewal.PreviousCredentialId || presented.CredentialId != peer.CredentialId
            || presented.NodeId != peer.FromNodeId || presented.StorageGeneration != peer.StorageGeneration
            || !presented.IsVoting || presented.IsAdmission || presented.ExpiresAtUtc <= time.GetUtcNow())
        {
            throw new UnauthorizedAccessException("VoterCredentialPeerOriginMismatch");
        }

        RevisionImage image = await authority.ConfirmSigningAsync(call, peer.FromNodeId, peer.StorageGeneration, token);
        if (peer.Term <= 0 || state.Consensus?.Term != peer.Term)
        {
            throw new UnauthorizedAccessException("VoterCredentialPeerTermChanged");
        }

        VoterCredentialPeerEvidence evidence = VoterCredentialPeerEvidence.Require(image, call, local.NodeId,
            local.StorageGeneration, issuerCredentialId, time.GetUtcNow(), authority.LegacyTrust);
        if (!string.Equals(presented.Fingerprint, evidence.SubjectCredential.Fingerprint, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(issuerFingerprint, evidence.IssuerCredential.Fingerprint, StringComparison.OrdinalIgnoreCase)
            || presented.Address.AbsoluteUri != request.Renewal.Issuance.PeerUri
            || EnrollmentPeerAuthority.RequireVoter(image, local.NodeId, local.StorageGeneration).Address != local.PublicUri)
        {
            throw new UnauthorizedAccessException("VoterCredentialPeerOriginMismatch");
        }

        return (image, evidence);
    }
}
