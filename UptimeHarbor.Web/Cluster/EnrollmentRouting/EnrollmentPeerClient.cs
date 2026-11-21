using System.Net;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Installation.Renewal;

namespace UptimeHarbor.Web.Cluster.EnrollmentRouting;

/// <summary>Typed voter RPC через прежний authenticated peer client; remote DTO не публикуются в projection.</summary>
public class EnrollmentPeerClient(ClusterRaftHandle handle, IClusterStateView state, RevisionStateStore revisions,
    IEnrollmentReadBarrier barrier, EnrollmentCommandReceiver receiver, IPeerTrustSource trust,
    IHttpClientFactory clients, IOptions<ClusterOptions> configured, TimeProvider time,
    DynamicPeerTls? tls = null) : IEnrollmentPeerClient, IVoterCredentialSignerClient, IVoterCredentialHandoffPeer
{
    async Task IVoterCredentialHandoffPeer.ConfirmAsync(OperationalNode witness, VoterCredentialHandoff handoff,
        DynamicPeerTls.Snapshot snapshot, RevisionImage image, CancellationToken token)
    {
        var (reply, _) = await ConfirmHandoffAsync<VoterCredentialHandoff, VoterCredentialHandoff>(witness, handoff,
            handoff, null, snapshot, image, EnrollmentPeerProtocol.HANDOFF, token);
        if (reply != handoff)
        {
            throw new UnauthorizedAccessException("VoterCredentialHandoffReplyMismatch");
        }
    }

    internal async Task<VoterCredentialRetirementEvidence> AttestRetirementAsync(OperationalNode witness,
        VoterCredentialRetirementIntent intent, DynamicPeerTls.Snapshot snapshot, RevisionImage image, CancellationToken token)
    {
        var (reply, presented) = await ConfirmHandoffAsync<VoterCredentialRetirementIntent, VoterCredentialRetirementEvidence>(
            witness, intent, intent.Handoff, intent, snapshot, image, EnrollmentPeerProtocol.RETIREMENT_HANDOFF, token);
        if (reply.NodeId != presented.NodeId || reply.StorageGeneration != presented.StorageGeneration
            || reply.CredentialId != presented.CredentialId || reply.Fingerprint != presented.Fingerprint)
        {
            throw new UnauthorizedAccessException("VoterCredentialHandoffReplyMismatch");
        }

        reply.Require(intent, image, state.Consensus ?? throw new UnauthorizedAccessException("VoterCredentialHandoffConsensusMissing"),
            time.GetUtcNow());
        return reply;
    }

    private async Task<(TResponse Response, PeerCredential Presented)> ConfirmHandoffAsync<TRequest, TResponse>(
        OperationalNode witness, TRequest request, VoterCredentialHandoff handoff, VoterCredentialRetirementIntent? retirement,
        DynamicPeerTls.Snapshot snapshot, RevisionImage image, string path, CancellationToken token)
    {
        ClusterOptions local = configured.Value;
        long recovery = revisions.RecoveryVersion;
        if (local.TlsBoundary != "Direct" || witness.NodeId == local.NodeId || snapshot.Quarantined
            || snapshot.CredentialId != handoff.IssueId || snapshot.CredentialRevision != handoff.CredentialRevision
            || snapshot.Fingerprint != handoff.Fingerprint)
        {
            throw new UnauthorizedAccessException("VoterCredentialHandoffOriginMismatch");
        }

        RequireCurrent();
        Uri address = new(witness.Address);
        PeerCredential? presented = null;
        PeerConnectionBinding binding = new(witness.NodeId, witness.StorageGeneration, address, snapshot.Fingerprint, credential =>
        {
            RequireCurrent();
            if (credential.NodeId != witness.NodeId || credential.StorageGeneration != witness.StorageGeneration
                || !credential.IsVoting || credential.IsAdmission || credential.Address != address || credential.ExpiresAtUtc <= time.GetUtcNow())
            {
                throw new UnauthorizedAccessException("VoterCredentialHandoffWitnessMismatch");
            }

            VoterCredentialHandoff.RequireLeaf(image, witness, credential.CredentialId, credential.Fingerprint, time.GetUtcNow());
            presented = credential;
        }) { ExpectedLocalSnapshot = snapshot };
        TResponse reply = await SendAsync<TRequest, TResponse>(witness.NodeId, path, request, token, binding);
        RequireCurrent();
        if (presented is null)
        {
            throw new UnauthorizedAccessException("VoterCredentialHandoffReplyMismatch");
        }

        VoterCredentialHandoff.RequireLeaf(image, witness, presented.CredentialId, presented.Fingerprint, time.GetUtcNow());
        return (reply, presented);

        void RequireCurrent()
        {
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(tls?.Capture(), snapshot) || !revisions.TryAtCurrent(image, recovery, static () => { })
                || EnrollmentPeerAuthority.RequireVoter(image, witness.NodeId, witness.StorageGeneration) != witness)
            {
                throw new UnauthorizedAccessException("VoterCredentialHandoffEvidenceChanged");
            }

            if (retirement is null)
            {
                handoff.RequireSubject(image, local.NodeId, local.StorageGeneration, time.GetUtcNow());
            }
            else
            {
                var subject = retirement.Require(image, time.GetUtcNow()).Renewal.Issuance;
                if (subject.NodeId != local.NodeId || subject.StorageGeneration != local.StorageGeneration)
                {
                    throw new UnauthorizedAccessException("VoterCredentialHandoffOriginMismatch");
                }
            }
        }
    }

    public async Task<CommandResult> AcceptAsync(EnrollmentCoordinatedRequest request, CancellationToken token)
    {
        await barrier.ConfirmAsync(token);
        Guid leader = Leader();
        CommandResult result;
        if (leader == configured.Value.NodeId)
        {
            result = await receiver.AcceptAsync(request, configured.Value.NodeId, configured.Value.StorageGeneration, token);
        }
        else
        {
            result = await SendAsync<EnrollmentCoordinatedRequest, CommandResult>(leader,
                EnrollmentPeerProtocol.COORDINATE, request, token);
        }

        await barrier.ConfirmAsync(token);
        CommandResult? applied = revisions.Current?.Results.GetValueOrDefault(request.Command.OperationId);
        if (!revisions.Available || applied is null || applied != result
            || result.Digest != RevisionCodec.Digest(request.Command))
        {
            throw new InvalidOperationException("EnrollmentDecisionNotApplied");
        }

        return applied;
    }

    public async Task<EnrollmentIssuedArtifact> IssueAsync(Guid issuerNodeId, EnrollmentSigningRequest request,
        CancellationToken token)
    {
        return await SendAsync<EnrollmentSigningRequest, EnrollmentIssuedArtifact>(issuerNodeId,
            EnrollmentPeerProtocol.ISSUE, request, token);
    }

    public async Task<OnlineGrant> SignGrantAsync(Guid issuerNodeId, OnlineGrantSigningRequest request, CancellationToken token)
    {
        return await SendAsync<OnlineGrantSigningRequest, OnlineGrant>(issuerNodeId,
            EnrollmentPeerProtocol.GRANT, request, token);
    }

    public async Task<EnrollmentIssuedArtifact> IssueAsync(Guid issuerNodeId, VoterCredentialSigningRequest call,
        CancellationToken token)
    {
        ClusterOptions local = configured.Value;
        VoterCredentialRequest request = call.ProofRequest.Request;
        VoterCredentialRenewal renewal = request.Renewal;
        DynamicPeerTls.Snapshot? localSnapshot = tls?.Capture();
        Guid credentialId = localSnapshot?.CredentialId ?? local.CredentialId;
        if (local.NodeId != renewal.Issuance.NodeId || local.StorageGeneration != renewal.Issuance.StorageGeneration
            || credentialId != renewal.PreviousCredentialId || issuerNodeId == local.NodeId
            || local.ClusterId != request.ClusterId || local.Generation != request.Generation
            || local.Compatibility != OnboardingProtocol.TRUST_COMPATIBILITY)
        {
            throw new UnauthorizedAccessException("VoterCredentialPeerOriginMismatch");
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10), time);
        using CancellationTokenSource guard = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        long recovery = revisions.RecoveryVersion;
        await barrier.ConfirmAsync(guard.Token);
        RevisionImage image = revisions.Current ?? throw new InvalidOperationException("EnrollmentPeerProjectionUnavailable");
        AcceptedEnrollmentIssuer issuer = EnrollmentPeerAuthority.RequireIssuer(image, renewal.Issuance.IssuerEpoch, time.GetUtcNow());
        OperationalNode node = EnrollmentPeerAuthority.RequireVoter(image, issuerNodeId, issuer.StorageGeneration);
        Uri address = new(node.Address);
        VoterCredentialLegacyTrust legacyTrust = new(local);
        VoterCredentialAuthorityPolicy.RequireAccepted(image, call, time.GetUtcNow(), legacyTrust);
        PeerCredential? presented = null;
        VoterCredentialPeerEvidence? before = null;
        PeerConnectionBinding binding = new(issuerNodeId, node.StorageGeneration, address,
            renewal.PreviousFingerprint, credential =>
            {
                VoterCredentialPeerEvidence initial = RequireEvidence(image, credential);
                RevisionImage current = CurrentImage();
                if (initial != RequireEvidence(current, credential) || !AtCurrent(current))
                {
                    throw new UnauthorizedAccessException("VoterCredentialPeerAuthorityChanged");
                }

                presented = credential;
                before = initial;
            }) { ExpectedLocalSnapshot = localSnapshot };

        EnrollmentIssuedArtifact artifact = await SendAsync<VoterCredentialSigningRequest, EnrollmentIssuedArtifact>(
            issuerNodeId, EnrollmentPeerProtocol.RENEW, call, guard.Token, binding);
        RevisionImage current = CurrentImage();
        if (presented is null || before != RequireEvidence(current, presented))
        {
            throw new UnauthorizedAccessException("VoterCredentialPeerAuthorityChanged");
        }

        guard.Token.ThrowIfCancellationRequested();
        VoterCredentialArtifactValidation.Require(current, call, artifact, time.GetUtcNow(), legacyTrust);
        if ((localSnapshot is not null && tls?.IsUsable(localSnapshot) != true) || !AtCurrent(current))
        {
            throw new UnauthorizedAccessException("VoterCredentialPeerAuthorityChanged");
        }

        return artifact;

        bool AtCurrent(RevisionImage accepted)
        {
            return revisions.TryAtCurrent(accepted, recovery, static () => { });
        }

        RevisionImage CurrentImage()
        {
            RevisionImage? accepted = revisions.Current;
            if (accepted is null || !revisions.Available || recovery != revisions.RecoveryVersion)
            {
                throw new UnauthorizedAccessException("VoterCredentialPeerAuthorityChanged");
            }

            return accepted;
        }

        VoterCredentialPeerEvidence RequireEvidence(RevisionImage accepted, PeerCredential credential)
        {
            VoterCredentialPeerEvidence evidence = VoterCredentialPeerEvidence.Require(accepted, call,
                issuerNodeId, node.StorageGeneration, credential.CredentialId, time.GetUtcNow(), legacyTrust);
            if (!string.Equals(credential.Fingerprint, evidence.IssuerCredential.Fingerprint, StringComparison.OrdinalIgnoreCase)
                || new Uri(EnrollmentPeerAuthority.RequireVoter(accepted, issuerNodeId, node.StorageGeneration).Address) != address)
            {
                throw new UnauthorizedAccessException("VoterCredentialPeerAuthorityChanged");
            }

            return evidence;
        }
    }

    internal async Task<TResponse> SendAsync<TRequest, TResponse>(Guid target, string path, TRequest payload,
        CancellationToken token, PeerConnectionBinding? binding = null, bool requireLeader = false)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10), time);
        using CancellationTokenSource guard = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        await barrier.ConfirmAsync(guard.Token);
        ConsensusStatus before = state.Consensus ?? throw new InvalidOperationException("EnrollmentPeerConsensusUnavailable");
        Guid leader = Leader();
        if (requireLeader && target != leader)
        {
            throw new InvalidOperationException("EnrollmentLeaderChanged");
        }

        RevisionImage image = revisions.Current ?? throw new InvalidOperationException("EnrollmentPeerProjectionUnavailable");
        OperationalNode node = image.Operations.Nodes.GetValueOrDefault(target)
            ?? throw new UnauthorizedAccessException("EnrollmentPeerTargetUnavailable");
        EnrollmentPeerAuthority.RequireVoter(image, target, node.StorageGeneration);
        Uri address = new(node.Address);
        if (binding is null)
        {
            IReadOnlyList<PeerCredential> peers = await trust.ListAsync(guard.Token);
            if (!peers.Any(item => item.NodeId == target && item.IsVoting && !item.IsAdmission
                && item.StorageGeneration == node.StorageGeneration && item.Address == address
                && item.ExpiresAtUtc > time.GetUtcNow()))
            {
                throw new UnauthorizedAccessException("EnrollmentPeerTrustUnavailable");
            }
        }
        else if (binding.Address != address || binding.StorageGeneration != node.StorageGeneration)
        {
            throw new UnauthorizedAccessException("VoterCredentialPeerAuthorityChanged");
        }
        byte[] bytes = StrictOnboardingJson.Encode(payload);
        if (bytes.Length > EnrollmentPeerProtocol.MAX_BYTES)
        {
            throw new InvalidDataException("EnrollmentPeerRequestLimit");
        }

        HttpClient client = clients.CreateClient(PeerClientHandler.CLIENT_NAME);
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(address, path));
        if (binding is not null)
        {
            request.Options.Set(PeerClientHandler.CONNECTION_BINDING, binding);
            request.Content = new SingleSendContent(bytes);
        }
        else
        {
            request.Content = new ByteArrayContent(bytes);
        }

        request.Content.Headers.ContentType = new("application/json");
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, guard.Token);
        if (response.StatusCode != System.Net.HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "application/json"
            || response.Content.Headers.ContentEncoding.Count != 0 || response.Content.Headers.ContentLength > EnrollmentPeerProtocol.MAX_BYTES
            || !response.Headers.TryGetValues(PeerHeaders.ENVELOPE, out IEnumerable<string>? envelopes))
        {
            throw new HttpRequestException("EnrollmentPeerUnavailable");
        }

        PeerEnvelope? reply = PeerHeaders.Decode(envelopes.Single());
        if (reply is null || reply.Term != before.Term)
        {
            throw new HttpRequestException("EnrollmentPeerUnavailable");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(guard.Token);
        byte[] received = await RevisionBatchCodec.ReadAsync(stream, EnrollmentPeerProtocol.MAX_BYTES, guard.Token);
        TResponse result = StrictOnboardingJson.Decode<TResponse>(received);
        await barrier.ConfirmAsync(guard.Token);
        ConsensusStatus? after = state.Consensus;
        if (after is null || before.Term != after.Term || before.MembershipVersion != after.MembershipVersion
            || leader != Leader() || !before.VotingNodes.SetEquals(after.VotingNodes))
        {
            throw new InvalidOperationException("EnrollmentPeerConsensusChanged");
        }

        return result;
    }

    internal Guid Leader()
    {
        ConsensusStatus? status = state.Consensus;
        var native = handle.Current?.Leader;
        if (status is null || native is null || !revisions.Available)
        {
            throw new InvalidOperationException("EnrollmentLeaderUnavailable");
        }

        if (status.IsLeader && !native.IsRemote)
        {
            return status.NodeId;
        }

        OperationalNode? leader = revisions.Current!.Operations.Nodes.Values.SingleOrDefault(item =>
            item.Voting && !item.Removed && item.RaftMemberId == native.Id.ToString());
        if (leader is null || !native.IsRemote)
        {
            throw new InvalidOperationException("EnrollmentLeaderUnavailable");
        }

        return leader.NodeId;
    }

    private class SingleSendContent(byte[] bytes) : HttpContent
    {
        private int started;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return SerializeToStreamAsync(stream, context, CancellationToken.None);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref started, 1) != 0)
            {
                throw new HttpRequestException("VoterCredentialPeerBodyAlreadyStarted");
            }

            return stream.WriteAsync(bytes.AsMemory(), cancellationToken).AsTask();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = bytes.Length;
            return true;
        }
    }
}
