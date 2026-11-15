using System.Text.Json;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Web.Cluster;

/// <summary>Свежий majority и authenticated capability каждого voter до регистрации нового anchor.</summary>
public class EnrollmentIssuerRegistrationBarrier(IEnrollmentReadBarrier majority, IClusterStateView view,
    RevisionStateStore revisions, IPeerTrustSource trust, IHttpClientFactory clients,
    IOptions<ClusterOptions> options, TimeProvider time) : IEnrollmentIssuerRegistrationBarrier
{
    public async ValueTask ConfirmAsync(CancellationToken token)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10), time);
        using CancellationTokenSource guard = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        await majority.ConfirmAsync(guard.Token);
        ConsensusStatus before = RequireCurrent();
        HashSet<Guid> voters = before.VotingNodes.ToHashSet();
        ClusterOperationsImage operations = revisions.Current!.Operations;
        IReadOnlyList<PeerCredential> peers = await trust.ListAsync(guard.Token);
        foreach (Guid voter in voters.Where(id => id != before.NodeId))
        {
            OperationalNode node = operations.Nodes[voter];
            PeerCredential? peer = peers.FirstOrDefault(item => item.NodeId == voter && item.IsVoting && !item.IsAdmission
                && item.StorageGeneration == node.StorageGeneration && item.Address == new Uri(node.Address)
                && item.ExpiresAtUtc > time.GetUtcNow());
            if (peer is null)
            {
                throw new InvalidOperationException("IssuerVoterCapabilityUnavailable");
            }

            await ConfirmPeerAsync(peer, before, guard.Token);
        }

        await majority.ConfirmAsync(guard.Token);
        guard.Token.ThrowIfCancellationRequested();
        ConsensusStatus after = RequireCurrent();
        if (before.Term != after.Term || before.MembershipVersion != after.MembershipVersion
            || !voters.SetEquals(after.VotingNodes))
        {
            throw new InvalidOperationException("IssuerVotersChanged");
        }

        foreach (Guid voter in voters)
        {
            OperationalNode original = operations.Nodes[voter];
            OperationalNode current = revisions.Current!.Operations.Nodes[voter];
            if (original.StorageGeneration != current.StorageGeneration || original.Address != current.Address)
            {
                throw new InvalidOperationException("IssuerVotersChanged");
            }
        }
    }

    private ConsensusStatus RequireCurrent()
    {
        ConsensusStatus? status = view.Consensus;
        RevisionImage? image = revisions.Current;
        if (options.Value.Compatibility != OnboardingProtocol.TRUST_COMPATIBILITY || !revisions.Available
            || status is null || image is null || !status.IsLeader || status.NodeId != options.Value.NodeId
            || status.ClusterId != image.ClusterId || status.Generation != image.Generation
            || image.ClusterId != options.Value.ClusterId || image.Generation != options.Value.Generation
            || status.LeadershipToken.IsCancellationRequested || status.ConsensusToken.IsCancellationRequested
            || !status.VotingNodes.Contains(status.NodeId) || image.Operations.MaintenanceId != Guid.Empty)
        {
            throw new InvalidOperationException("IssuerVoterCapabilityUnavailable");
        }

        HashSet<Guid> accepted = image.Operations.Nodes.Values.Where(item => item.Voting && !item.Removed)
            .Select(item => item.NodeId).ToHashSet();
        if (!accepted.SetEquals(status.VotingNodes) || status.MembershipVersion != image.Operations.MembershipVersion)
        {
            throw new InvalidOperationException("IssuerVotersChanged");
        }

        return status;
    }

    private async Task ConfirmPeerAsync(PeerCredential peer, ConsensusStatus expected, CancellationToken token)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3), time);
        using CancellationTokenSource guard = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token,
            expected.LeadershipToken, expected.ConsensusToken);
        HttpClient client = clients.CreateClient(PeerClientHandler.CLIENT_NAME);
        using HttpResponseMessage response = await client.GetAsync(new Uri(peer.Address, "/internal/cluster/v1/health"),
            HttpCompletionOption.ResponseHeadersRead, guard.Token);
        int maximum = options.Value.HealthMaxBytes;
        if (maximum is < 1 or > 65536 || !response.IsSuccessStatusCode || response.Content.Headers.ContentLength > maximum)
        {
            throw new InvalidOperationException("IssuerVoterCapabilityUnavailable");
        }

        await using Stream body = await response.Content.ReadAsStreamAsync(guard.Token);
        byte[] bytes = new byte[maximum + 1];
        int length = 0;
        while (length < bytes.Length)
        {
            int read = await body.ReadAsync(bytes.AsMemory(length), guard.Token);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        if (length > maximum)
        {
            throw new InvalidOperationException("IssuerVoterCapabilityUnavailable");
        }

        PeerHealthSnapshot? health = JsonSerializer.Deserialize<PeerHealthSnapshot>(bytes.AsSpan(0, length), JsonSerializerOptions.Web);
        if (health is null || health.NodeId != peer.NodeId || health.BootId == Guid.Empty || health.EnrollmentTrustVersion != 1
            || health.Term != expected.Term || health.MembershipVersion != expected.MembershipVersion
            || health.LeaderNodeId != expected.NodeId)
        {
            throw new InvalidOperationException("IssuerVoterCapabilityUnavailable");
        }
    }
}
