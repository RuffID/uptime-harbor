using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;

namespace UptimeHarbor.Web.Cluster.EnrollmentRouting;

/// <summary>Typed voter RPC через прежний authenticated peer client; remote DTO не публикуются в projection.</summary>
public class EnrollmentPeerClient(ClusterRaftHandle handle, IClusterStateView state, RevisionStateStore revisions,
    IEnrollmentReadBarrier barrier, EnrollmentCommandReceiver receiver, IPeerTrustSource trust,
    IHttpClientFactory clients, IOptions<ClusterOptions> configured, TimeProvider time) : IEnrollmentPeerClient
{
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

    private async Task<TResponse> SendAsync<TRequest, TResponse>(Guid target, string path, TRequest payload, CancellationToken token)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10), time);
        using CancellationTokenSource guard = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        await barrier.ConfirmAsync(guard.Token);
        ConsensusStatus before = state.Consensus ?? throw new InvalidOperationException("EnrollmentPeerConsensusUnavailable");
        Guid leader = Leader();
        RevisionImage image = revisions.Current ?? throw new InvalidOperationException("EnrollmentPeerProjectionUnavailable");
        OperationalNode node = image.Operations.Nodes.GetValueOrDefault(target)
            ?? throw new UnauthorizedAccessException("EnrollmentPeerTargetUnavailable");
        EnrollmentPeerAuthority.RequireVoter(image, target, node.StorageGeneration);
        IReadOnlyList<PeerCredential> peers = await trust.ListAsync(guard.Token);
        PeerCredential peer = peers.FirstOrDefault(item => item.NodeId == target && item.IsVoting && !item.IsAdmission
            && item.StorageGeneration == node.StorageGeneration && item.Address == new Uri(node.Address)
            && item.ExpiresAtUtc > time.GetUtcNow()) ?? throw new UnauthorizedAccessException("EnrollmentPeerTrustUnavailable");
        byte[] bytes = StrictOnboardingJson.Encode(payload);
        if (bytes.Length > EnrollmentPeerProtocol.MAX_BYTES)
        {
            throw new InvalidDataException("EnrollmentPeerRequestLimit");
        }

        HttpClient client = clients.CreateClient(PeerClientHandler.CLIENT_NAME);
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(peer.Address, path));
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new("application/json");
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, guard.Token);
        if (response.StatusCode != System.Net.HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "application/json"
            || response.Content.Headers.ContentEncoding.Count != 0 || response.Content.Headers.ContentLength > EnrollmentPeerProtocol.MAX_BYTES
            || !response.Headers.TryGetValues(PeerHeaders.ENVELOPE, out IEnumerable<string>? envelopes)
            || PeerHeaders.Decode(envelopes.Single())?.Term != before.Term)
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

    private Guid Leader()
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
}
