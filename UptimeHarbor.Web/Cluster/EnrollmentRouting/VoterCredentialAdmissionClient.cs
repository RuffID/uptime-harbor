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

public sealed class VoterCredentialAdmissionClient(EnrollmentPeerClient peers, VoterCredentialAdmission receiver,
    RevisionStateStore revisions, IClusterStateView state, IEnrollmentReadBarrier barrier, InstallationState installation,
    DynamicPeerTls tls, IOptions<ClusterOptions> options, TimeProvider time)
    : IVoterCredentialAdmissionClient, IVoterCredentialRetirementClient
{
    public async Task<string> IssueNonceAsync(VoterCredentialNonceCall call, CancellationToken token)
    {
        var (nonce, _) = await RouteAsync(call, call.Request, EnrollmentPeerProtocol.ROTATION_NONCE, receiver.IssueNonceAsync, token);
        if (!EnrollmentProofs.IsChallenge(nonce))
        {
            throw new UnauthorizedAccessException("VoterCredentialNonceReplyInvalid");
        }

        return nonce;
    }

    public async Task<CommandResult> AuthorizeAsync(VoterCredentialAuthorizeCall call, CancellationToken token)
    {
        var (result, image) = await RouteAsync(call, call.ProofRequest.Request, EnrollmentPeerProtocol.ROTATION_AUTHORIZE,
            receiver.AuthorizeAsync, token);
        return RequireDecision(image, call.Command, result);
    }

    public async Task<CommandResult> BindAsync(VoterCredentialBindCall call, CancellationToken token)
    {
        var (result, image) = await RouteAsync(call, call.Signing.ProofRequest.Request, EnrollmentPeerProtocol.ROTATION_BIND,
            receiver.BindAsync, token);
        return RequireDecision(image, call.Command, result);
    }

    public async Task<CommandResult> RetireAsync(VoterCredentialRetirementIntent intent, CancellationToken token)
    {
        if (intent.Command is not { Action: "RetireOldCredential", EnrollmentTrust.Rotation: { } payload })
        {
            throw new UnauthorizedAccessException("VoterCredentialRetirementIntentInvalid");
        }

        var (result, image) = await RouteAsync(new VoterCredentialRetirementCall(intent, null),
            intent.Command.ClusterId, intent.Command.ClusterGeneration, payload.Renewal.Issuance, intent.Handoff.IssueId,
            intent.Handoff.Fingerprint, intent.Handoff.CredentialRevision, EnrollmentPeerProtocol.ROTATION_RETIRE,
            receiver.RetireAsync, token, async (call, snapshot, cut, consensus) =>
            {
                intent.Require(cut, time.GetUtcNow());
                VoterCredentialRetirementEvidence.RequireMembership(cut, consensus);
                if (consensus.VotingNodes.Count == 1)
                {
                    return call;
                }

                foreach (OperationalNode witness in cut.Operations.Nodes.Values.Where(node =>
                    node.NodeId != consensus.NodeId && node.Voting && !node.Removed && node.Activated).OrderBy(node => node.NodeId))
                {
                    try
                    {
                        return call with { Evidence = await peers.AttestRetirementAsync(witness, intent, snapshot, cut, token) };
                    }
                    catch (Exception error) when (!token.IsCancellationRequested
                        && error is HttpRequestException or TimeoutException or OperationCanceledException)
                    {
                        // Another current voter can attest independently.
                    }
                }

                throw new UnauthorizedAccessException("VoterCredentialHandoffPeerUnavailable");
            });

        CommandResult? applied = intent.ReadDecision(image, time.GetUtcNow());
        return applied is not null && applied == result
            ? applied
            : throw new UnauthorizedAccessException("VoterCredentialAdmissionReplyChanged");
    }

    private Task<(TResponse Response, RevisionImage Applied)> RouteAsync<TRequest, TResponse>(TRequest call,
        VoterCredentialRequest request, string path, Func<TRequest, VoterCredentialAdmissionOrigin, CancellationToken, Task<TResponse>> accept,
        CancellationToken token)
    {
        VoterCredentialRenewal renewal = request.Renewal;
        return RouteAsync(call, request.ClusterId, request.Generation, renewal.Issuance, renewal.PreviousCredentialId,
            renewal.PreviousFingerprint, renewal.PreviousCredentialRevision, path, accept, token);
    }

    private async Task<(TResponse Response, RevisionImage Applied)> RouteAsync<TRequest, TResponse>(TRequest call,
        Guid clusterId, long generation, EnrollmentIssuance subject, Guid leafId, string fingerprint, long revision, string path,
        Func<TRequest, VoterCredentialAdmissionOrigin, CancellationToken, Task<TResponse>> accept, CancellationToken token,
        Func<TRequest, DynamicPeerTls.Snapshot, RevisionImage, ConsensusStatus, Task<TRequest>>? prepare = null)
    {
        ClusterOptions local = options.Value;
        LocalInstallationProfile? profile = installation.Current;
        DynamicPeerTls.Snapshot? snapshot = tls.Capture();
        if (local.TlsBoundary != "Direct" || !installation.CommonOpen
            || profile is not { Mode: "Bootstrap", Phase: InstallationPhase.ClusterReady }
            || local.NodeId != subject.NodeId || local.StorageGeneration != subject.StorageGeneration
            || profile.NodeId != subject.NodeId || profile.StorageGeneration != subject.StorageGeneration
            || profile.PeerAddress != subject.PeerUri || local.PublicUri != subject.PeerUri
            || profile.ClusterId != clusterId || profile.ClusterGeneration != generation
            || local.ClusterId != clusterId || local.Generation != generation
            || snapshot is not { Quarantined: false } || snapshot.Generation != profile.RuntimeGeneration
            || snapshot.CredentialId != leafId || snapshot.Fingerprint != fingerprint
            || snapshot.CredentialRevision != revision)
        {
            throw new UnauthorizedAccessException("VoterCredentialAdmissionOriginMismatch");
        }

        long recovery = revisions.RecoveryVersion;
        await barrier.ConfirmAsync(token);
        ConsensusStatus before = state.Consensus ?? throw new InvalidOperationException("EnrollmentLeaderUnavailable");
        Guid leader = peers.Leader();
        RevisionImage image = RequireCurrent();
        if (prepare is not null)
        {
            call = await prepare(call, snapshot, image, before);
            image = RequireCurrent();
        }

        TResponse response;
        PeerCredential? presented = null;
        if (leader == local.NodeId)
        {
            response = await accept(call, new(snapshot), token);
        }
        else
        {
            OperationalNode node = image.Operations.Nodes.GetValueOrDefault(leader)
                ?? throw new UnauthorizedAccessException("VoterCredentialLeaderUnavailable");
            Uri address = new(node.Address);
            PeerConnectionBinding binding = new(leader, node.StorageGeneration, address, snapshot.Fingerprint, credential =>
            {
                RequireServer(RequireCurrent(), credential, node);
                presented = credential;
            }) { ExpectedLocalSnapshot = snapshot };
            response = await peers.SendAsync<TRequest, TResponse>(leader, path, call, token, binding, requireLeader: true);
            RequireServer(RequireCurrent(), presented ?? throw new UnauthorizedAccessException("VoterCredentialLeaderTlsMissing"), node);
        }

        await barrier.ConfirmAsync(token);
        RevisionImage applied = RequireCurrent();
        if (presented is not null)
        {
            RequireServer(applied, presented, image.Operations.Nodes[leader]);
        }

        if (!revisions.TryAtCurrent(applied, recovery, static () => { }))
        {
            throw new UnauthorizedAccessException("VoterCredentialAdmissionProjectionChanged");
        }

        return (response, applied);

        RevisionImage RequireCurrent()
        {
            token.ThrowIfCancellationRequested();
            RuntimeGenerationContext.Verify();
            ConsensusStatus? current = state.Consensus;
            RevisionImage? cut = revisions.Current;
            if (cut is null || current is null || current.Term != before.Term
                || current.ClusterId != clusterId || current.Generation != generation
                || current.NodeId != local.NodeId || current.IsLeader != before.IsLeader
                || current.MembershipVersion != before.MembershipVersion || cut.Operations.MembershipVersion != current.MembershipVersion
                || current.ConsensusToken != before.ConsensusToken || current.ConsensusToken.IsCancellationRequested
                || current.IsLeader && (current.LeadershipToken != before.LeadershipToken || current.LeadershipToken.IsCancellationRequested)
                || !before.VotingNodes.SetEquals(current.VotingNodes) || !current.VotingNodes.Contains(leader)
                || !current.VotingNodes.Contains(local.NodeId) || peers.Leader() != leader
                || !ReferenceEquals(installation.Current, profile) || !installation.CommonOpen
                || !ReferenceEquals(tls.Capture(), snapshot) || !revisions.TryAtCurrent(cut, recovery, static () => { }))
            {
                throw new UnauthorizedAccessException("VoterCredentialAdmissionAuthorityChanged");
            }

            OperationalNode origin = EnrollmentPeerAuthority.RequireVoter(cut, local.NodeId, local.StorageGeneration);
            VoterCredentialHandoff.RequireLeaf(cut, origin, leafId, fingerprint, time.GetUtcNow());
            return cut;
        }
    }

    private void RequireServer(RevisionImage image, PeerCredential presented, OperationalNode node)
    {
        DateTimeOffset now = time.GetUtcNow();
        if (EnrollmentPeerAuthority.RequireVoter(image, node.NodeId, node.StorageGeneration) != node
            || presented.NodeId != node.NodeId || presented.StorageGeneration != node.StorageGeneration
            || !presented.IsVoting || presented.IsAdmission || presented.ExpiresAtUtc <= now
            || presented.Address != new Uri(node.Address))
        {
            throw new UnauthorizedAccessException("VoterCredentialLeaderTlsChanged");
        }

        VoterCredentialHandoff.RequireLeaf(image, node, presented.CredentialId, presented.Fingerprint, now);
    }

    private static CommandResult RequireDecision(RevisionImage image, RevisionCommand command, CommandResult reply)
    {
        CommandResult? result = VoterCredentialAdmission.CheckDecision(image, command);
        return result is not null && result == reply
            ? result
            : throw new UnauthorizedAccessException("VoterCredentialAdmissionReplyChanged");
    }
}
