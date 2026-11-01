using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Web.Cluster;

public class DotNextMembershipProtocol(ClusterPeerRuntime runtime, ClusterRuntimeState state,
    IPeerTrustSource trust) : IMembershipProtocol
{
    public async ValueTask<MembershipSnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        await runtime.RefreshStatusAsync(cancellationToken);
        ConsensusStatus? status = state.Consensus;
        IRaftHttpCluster? raft = runtime.Raft;
        if (status is null || raft is null || !status.IsLeader || status.ConsensusToken.IsCancellationRequested)
        {
            return null;
        }

        await raft.ApplyReadBarrierAsync(ReadBarrierType.Strong, cancellationToken);
        await runtime.RefreshStatusAsync(cancellationToken);
        ConsensusStatus? refreshed = state.Consensus;
        if (runtime.Raft != raft || refreshed is null || !refreshed.IsLeader
            || refreshed.Term != status.Term || refreshed.MembershipVersion != status.MembershipVersion
            || refreshed.ConsensusToken.IsCancellationRequested || refreshed.LeadershipToken.IsCancellationRequested)
        {
            return null;
        }

        IReadOnlyList<PeerCredential> peers = await trust.ListAsync(cancellationToken);
        Dictionary<Guid, Uri> voters = [];
        foreach (Guid nodeId in status.VotingNodes)
        {
            if (nodeId == status.NodeId)
            {
                voters.Add(nodeId, raft.LocalMemberAddress);
                continue;
            }

            PeerCredential? peer = peers.FirstOrDefault(peer => peer.NodeId == nodeId);
            if (peer is null)
            {
                return null;
            }

            voters.Add(nodeId, peer.Address);
        }

        return new(status.MembershipVersion, voters);
    }

    public async ValueTask<bool> ChangeAsync(AcceptedMembershipChange change, CancellationToken cancellationToken)
    {
        await runtime.RefreshStatusAsync(cancellationToken);
        await runtime.MembershipLock.WaitAsync(cancellationToken);
        try
        {
            IRaftHttpCluster? raft = runtime.Raft;
            ConsensusStatus? status = state.Consensus;
            if (raft is null || status is null || !status.IsLeader
                || status.MembershipVersion != change.ExpectedMembershipVersion
                || status.ConsensusToken.IsCancellationRequested
                || await runtime.ReadMembershipVersionAsync(cancellationToken) != change.ExpectedMembershipVersion)
            {
                return false;
            }

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, status.LeadershipToken, status.ConsensusToken);
            linked.CancelAfter(TimeSpan.FromSeconds(10));
            if (change.Remove)
            {
                if (status.VotingNodes.Count <= 1)
                {
                    return false;
                }

                return await raft.RemoveMemberAsync(change.Address, linked.Token);
            }

            return await raft.AddMemberAsync(change.Address, linked.Token);
        }
        finally
        {
            runtime.MembershipLock.Release();
        }
    }
}
