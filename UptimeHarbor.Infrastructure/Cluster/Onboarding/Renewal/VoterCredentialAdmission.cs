using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Installation.Renewal;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

public sealed record VoterCredentialAdmissionOrigin(DynamicPeerTls.Snapshot Local, PeerEnvelope? Peer = null,
    PeerCredential? Presented = null);

/// <summary>Typed leader admission for a verified Direct TLS pair or the published local credential.</summary>
public sealed class VoterCredentialAdmission(VoterCredentialRenewalAuthority authority, RevisionDispatcher dispatcher,
    RevisionStateStore revisions, IClusterStateView state, EnrollmentNonces nonces, InstallationState installation,
    DynamicPeerTls tls, IOptions<ClusterOptions> options, TimeProvider time)
{
    private const string NONCE_SCOPE = "AuthorizeVoterCredential";

    public async Task<string> IssueNonceAsync(VoterCredentialNonceCall call, VoterCredentialAdmissionOrigin origin,
        CancellationToken token)
    {
        AdmissionCut cut = await ReadAsync(call.Request, call.Process, origin, token);
        if (call.Process.Nonce.Length != 0 || call.Process.Signature.Length != 0)
        {
            throw new UnauthorizedAccessException("VoterCredentialNonceRequestInvalid");
        }

        RevisionCommand command = VoterCredentialAuthorizationCommand.Create(call.Request);
        bool boundReplay = CheckDecision(cut.Image, command) is { Outcome: OperationOutcome.Committed };
        VoterCredentialAuthorityPolicy.RequireCurrent(cut.Image, call.Request, time.GetUtcNow(), boundReplay);
        VoterCredentialProofs.RequireProcess(cut.Image, call.Request, call.Process, time.GetUtcNow());
        RequireUnchanged(cut, origin, token);
        return nonces.Issue(call.Request.Renewal.Issuance.IssueId, call.Process.BootId, call.Process.BootChallenge,
            call.Process.ProcessEpoch, NONCE_SCOPE, NonceDigest(cut, origin, call.Request));
    }

    public Task<CommandResult> AuthorizeAsync(VoterCredentialAuthorizeCall call, VoterCredentialAdmissionOrigin origin,
        CancellationToken token)
    {
        VoterCredentialRequest request = call.ProofRequest.Request;
        if (call.Command != VoterCredentialAuthorizationCommand.Create(request))
        {
            throw new UnauthorizedAccessException("VoterCredentialAuthorizeCommandChanged");
        }

        return AcceptAsync(call.Command, request, call.ProofRequest.Proof, origin, (cut, now) =>
        {
            CommandResult? result = CheckDecision(cut.Image, call.Command);
            VoterCredentialProofs.Require(cut.Image, call.ProofRequest, now, authority.LegacyTrust,
                result?.Outcome == OperationOutcome.Committed);
            if (result is null)
            {
                VoterCredentialProof proof = call.ProofRequest.Proof;
                EnrollmentProof challenge = new(1, request.OperationId, NONCE_SCOPE, proof.BootId, proof.BootChallenge,
                    proof.ProcessEpoch, proof.Nonce, NonceDigest(cut, origin, request), string.Empty);
                if (!nonces.Consume(request.Renewal.Issuance.IssueId, challenge))
                {
                    throw new UnauthorizedAccessException("VoterCredentialNonceInvalid");
                }
            }
        }, token);
    }

    public Task<CommandResult> BindAsync(VoterCredentialBindCall call, VoterCredentialAdmissionOrigin origin,
        CancellationToken token)
    {
        VoterCredentialRequest request = call.Signing.ProofRequest.Request;
        EnrollmentIssuedArtifact artifact = call.Artifact with { CertificateDer = call.Artifact.CertificateDer.ToArray() };
        if (call.Command != VoterCredentialBindCommand.Create(request, artifact, call.Command.OperationId))
        {
            throw new UnauthorizedAccessException("VoterCredentialBindCommandChanged");
        }

        return AcceptAsync(call.Command, request, call.BindProof, origin, (cut, now) =>
        {
            bool boundReplay = CheckDecision(cut.Image, call.Command)?.Outcome == OperationOutcome.Committed;
            VoterCredentialArtifactValidation.Require(cut.Image, call.Signing, artifact, now, authority.LegacyTrust, boundReplay);
            VoterCredentialProofs.RequireBind(cut.Image, call, now, authority.LegacyTrust, boundReplay);
            if (boundReplay)
            {
                OperationalNode node = cut.Image.Operations.Nodes[request.Renewal.Issuance.NodeId];
                VoterCredentialHandoff.RequireLeaf(cut.Image, node, artifact.IssueId, artifact.CertificateDigest, now);
            }
        }, token);
    }

    public Task<CommandResult> RetireAsync(VoterCredentialRetirementCall call, VoterCredentialAdmissionOrigin origin,
        CancellationToken token)
    {
        if (call.Intent is not { Handoff: not null, Command: { Action: "RetireOldCredential",
            EnrollmentTrust.Rotation.Renewal.Issuance: { } subject } command } intent)
        {
            throw new UnauthorizedAccessException("VoterCredentialRetirementIntentInvalid");
        }

        return dispatcher.AcceptVoterCredentialAsync(command, async ct =>
        {
            AdmissionCut cut = await ReadAsync(command.ClusterId, command.ClusterGeneration, subject,
                intent.Handoff.IssueId, intent.Handoff.Fingerprint, intent.Handoff.CredentialRevision, origin, ct);
            DateTimeOffset now = time.GetUtcNow();
            intent.Require(cut.Image, now);
            VoterCredentialRetirementEvidence.RequireMembership(cut.Image, cut.Leader);
            if (cut.Leader.VotingNodes.Count == 1)
            {
                if (origin.Peer is not null || call.Evidence is not null)
                {
                    throw new UnauthorizedAccessException("VoterCredentialRetirementSingletonRequired");
                }

                RequirePublishedKey(origin.Local, intent.Handoff.Fingerprint);
            }
            else
            {
                VoterCredentialRetirementEvidence evidence = call.Evidence
                    ?? throw new UnauthorizedAccessException("VoterCredentialRetirementWitnessRequired");
                evidence.Require(intent, cut.Image, cut.Leader, now);
            }

            RequireUnchanged(cut, origin, ct);
            return (cut.Image, cut.Recovery, cut.Leader, now);
        }, token);
    }

    private static void RequirePublishedKey(DynamicPeerTls.Snapshot snapshot, string fingerprint)
    {
        byte[] challenge = RandomNumberGenerator.GetBytes(32);
        using ECDsa? privateKey = snapshot.Certificate.GetECDsaPrivateKey();
        using ECDsa? publicKey = snapshot.Certificate.GetECDsaPublicKey();
        if (EnrollmentCertificates.Hash(snapshot.Certificate.RawData) != fingerprint || privateKey is null || publicKey is null
            || !publicKey.VerifyData(challenge, privateKey.SignData(challenge, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256))
        {
            throw new UnauthorizedAccessException("VoterCredentialHandoffKeyUnavailable");
        }
    }

    public static CommandResult? CheckDecision(RevisionImage image, RevisionCommand command)
    {
        CommandResult? result = RevisionDispatcher.ReadVoterCredentialDecision(image, command);
        VoterCredentialCommand payload = command.EnrollmentTrust!.Rotation!;
        Guid issueId = payload.Renewal.Issuance.IssueId;
        VoterCredentialRotation? rotation = image.OnboardingContracts?.VoterCredentialRotations?.GetValueOrDefault(issueId);
        bool exactAuthorization = rotation is not null && rotation.Renewal == payload.Renewal
            && rotation.AuthorizationOperationId == command.OperationId && rotation.AuthorizationDigest == result?.Digest
            && rotation.AuthorizedIndex == result?.Index;
        bool exactBinding = rotation is not null && rotation.Renewal == payload.Renewal
            && rotation.BindOperationId == command.OperationId && rotation.BindDigest == result?.Digest
            && rotation.CredentialRevision == result?.Index
            && image.Operations.Credentials.GetValueOrDefault(issueId) is { } leaf
            && payload.Credential is { } expected && leaf.Fingerprint == expected.Fingerprint
            && leaf.PublicCertificate == expected.PublicCertificate;
        bool valid = command.Action switch
        {
            "AuthorizeVoterCredential" => result?.Outcome == OperationOutcome.Committed ? exactAuthorization : rotation is null,
            "BindVoterCredential" => result?.Outcome == OperationOutcome.Committed ? exactBinding : rotation?.CredentialRevision == 0,
            _ => false
        };
        if (!valid)
        {
            throw new UnauthorizedAccessException("VoterCredentialAdmissionDecisionConflict");
        }

        return result;
    }

    private async Task<CommandResult> AcceptAsync(RevisionCommand command, VoterCredentialRequest request,
        VoterCredentialProof proof, VoterCredentialAdmissionOrigin origin, Action<AdmissionCut, DateTimeOffset> verify,
        CancellationToken token)
    {
        return await dispatcher.AcceptVoterCredentialAsync(command, async cancellationToken =>
        {
            AdmissionCut cut = await ReadAsync(request, proof, origin, cancellationToken);
            DateTimeOffset now = time.GetUtcNow();
            verify(cut, now);
            now = time.GetUtcNow();
            VoterCredentialProofs.RequireProcess(cut.Image, request, proof, now);
            RequireUnchanged(cut, origin, cancellationToken);
            return (cut.Image, cut.Recovery, cut.Leader, now);
        }, token);
    }

    private async Task<AdmissionCut> ReadAsync(VoterCredentialRequest request, VoterCredentialProof proof,
        VoterCredentialAdmissionOrigin origin, CancellationToken token)
    {
        VoterCredentialRenewal renewal = request.Renewal;
        EnrollmentIssuance subject = renewal.Issuance;
        AdmissionCut cut = await ReadAsync(request.ClusterId, request.Generation, subject, renewal.PreviousCredentialId,
            renewal.PreviousFingerprint, renewal.PreviousCredentialRevision, origin, token);
        if (proof.OriginNodeId != subject.NodeId || proof.OriginStorageGeneration != subject.StorageGeneration
            || origin.Peer is null && proof.BootId != cut.Profile.BootId)
        {
            throw new UnauthorizedAccessException("VoterCredentialAdmissionOriginMismatch");
        }

        return cut;
    }

    private async Task<AdmissionCut> ReadAsync(Guid clusterId, long generation, EnrollmentIssuance subject, Guid leafId,
        string fingerprint, long credentialRevision, VoterCredentialAdmissionOrigin origin, CancellationToken token)
    {
        ClusterOptions local = options.Value;
        LocalInstallationProfile? profile = installation.Current;
        if (local.TlsBoundary != "Direct" || !installation.CommonOpen
            || profile is not { Phase: InstallationPhase.ClusterReady, Mode: "Bootstrap" }
            || profile.NodeId != local.NodeId || profile.StorageGeneration != local.StorageGeneration
            || profile.ClusterId != local.ClusterId || profile.ClusterGeneration != local.Generation
            || profile.PeerAddress != local.PublicUri || origin.Local.Quarantined || origin.Local.Generation != profile.RuntimeGeneration
            || !ReferenceEquals(tls.Capture(), origin.Local) || origin.Local.CredentialId is not Guid serverId)
        {
            throw new UnauthorizedAccessException("VoterCredentialAdmissionDirectRequired");
        }

        ConsensusStatus leader = state.Consensus ?? throw new UnauthorizedAccessException("VoterCredentialLeaderRequired");
        long recovery = revisions.RecoveryVersion;
        RevisionImage image = await authority.ReadFreshAsync(token);
        if (!leader.IsLeader || leader.NodeId != local.NodeId || leader.Term <= 0 || leader.Term < image.Term
            || leader.ClusterId != image.ClusterId || leader.Generation != image.Generation
            || leader.MembershipVersion != image.Operations.MembershipVersion || leader.CommittedIndex > image.Index
            || !leader.VotingNodes.Contains(local.NodeId) || !leader.VotingNodes.Contains(subject.NodeId)
            || clusterId != image.ClusterId || generation != image.Generation)
        {
            throw new UnauthorizedAccessException("VoterCredentialLeaderRequired");
        }

        DateTimeOffset now = time.GetUtcNow();
        OperationalNode server = EnrollmentPeerAuthority.RequireVoter(image, local.NodeId, local.StorageGeneration);
        VoterCredentialHandoff.RequireLeaf(image, server, serverId, origin.Local.Fingerprint, now);
        OperationalNode node = EnrollmentPeerAuthority.RequireVoter(image, subject.NodeId, subject.StorageGeneration);
        VoterCredentialHandoff.RequireLeaf(image, node, leafId, fingerprint, now);
        if (server.Address != local.PublicUri || node.Address != subject.PeerUri)
        {
            throw new UnauthorizedAccessException("VoterCredentialAdmissionOriginMismatch");
        }

        if (origin.Peer is { } peer)
        {
            if (subject.NodeId == local.NodeId || peer.FromNodeId != subject.NodeId || peer.ToNodeId != local.NodeId
                || peer.StorageGeneration != subject.StorageGeneration || peer.ClusterId != image.ClusterId
                || peer.Generation != image.Generation || peer.Term != leader.Term || peer.Compatibility != local.Compatibility
                || peer.CredentialId != leafId || origin.Presented is not { } presented
                || presented.NodeId != peer.FromNodeId || presented.StorageGeneration != peer.StorageGeneration
                || presented.CredentialId != peer.CredentialId || presented.Fingerprint != fingerprint
                || presented.Address != new Uri(node.Address) || !presented.IsVoting || presented.IsAdmission
                || presented.ExpiresAtUtc <= now)
            {
                throw new UnauthorizedAccessException("VoterCredentialAdmissionOriginMismatch");
            }
        }
        else if (origin.Presented is not null || subject.NodeId != local.NodeId || subject.StorageGeneration != local.StorageGeneration
            || origin.Local.CredentialRevision != credentialRevision
            || serverId != leafId || origin.Local.Fingerprint != fingerprint)
        {
            throw new UnauthorizedAccessException("VoterCredentialAdmissionOriginMismatch");
        }

        AdmissionCut cut = new(image, recovery, leader, profile);
        RequireUnchanged(cut, origin, token);
        return cut;
    }

    private void RequireUnchanged(AdmissionCut cut, VoterCredentialAdmissionOrigin origin, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RuntimeGenerationContext.Verify();
        ConsensusStatus leader = cut.Leader;
        ConsensusStatus? current = state.Consensus;
        if (current is not { IsLeader: true } || current.NodeId != leader.NodeId || current.Term != leader.Term
            || current.ClusterId != leader.ClusterId || current.Generation != leader.Generation
            || current.MembershipVersion != leader.MembershipVersion || current.CommittedIndex > cut.Image.Index
            || !current.VotingNodes.SetEquals(leader.VotingNodes) || current.ConsensusToken != leader.ConsensusToken
            || current.LeadershipToken != leader.LeadershipToken || leader.ConsensusToken.IsCancellationRequested
            || leader.LeadershipToken.IsCancellationRequested || !ReferenceEquals(installation.Current, cut.Profile)
            || !installation.CommonOpen || !ReferenceEquals(tls.Capture(), origin.Local)
            || !revisions.TryAtCurrent(cut.Image, cut.Recovery, static () => { }))
        {
            throw new UnauthorizedAccessException("VoterCredentialAdmissionAuthorityChanged");
        }
    }

    private static string NonceDigest(AdmissionCut cut, VoterCredentialAdmissionOrigin origin, VoterCredentialRequest request)
    {
        return EnrollmentCertificates.Hash(StrictOnboardingJson.Encode(new
        {
            Intent = VoterCredentialProofs.Digest(request), cut.Recovery, cut.Leader.Term, cut.Leader.MembershipVersion,
            ReceivingNode = cut.Leader.NodeId, ReceivingStorage = cut.Profile.StorageGeneration,
            origin.Local.Epoch, origin.Local.Generation, origin.Local.CredentialId, origin.Local.Fingerprint
        }));
    }

    private sealed record AdmissionCut(RevisionImage Image, long Recovery, ConsensusStatus Leader, LocalInstallationProfile Profile);
}
