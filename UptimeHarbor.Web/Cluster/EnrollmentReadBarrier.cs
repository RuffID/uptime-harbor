using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Web.Cluster;

/// <summary>Отсутствие reservation можно утверждать только после свежего majority barrier.</summary>
public class EnrollmentReadBarrier(ClusterRaftHandle handle, IClusterStateView state,
    RevisionStateStore revisions, NodeReadiness readiness, IOptions<ClusterOptions> options) : IEnrollmentReadBarrier
{
    public async ValueTask ConfirmAsync(CancellationToken token)
    {
        var raft = handle.Current;
        ConsensusStatus? before = state.Consensus;
        var leader = raft?.Leader;
        if (raft is null || before is null || leader is null || leader.IsRemote == before.IsLeader
            || before.NodeId != options.Value.NodeId || before.Term != raft.AuditTrail.Term
            || !before.VotingNodes.Contains(before.NodeId)
            || options.Value.Compatibility != OnboardingProtocol.TRUST_COMPATIBILITY || !Healthy())
        {
            throw new InvalidOperationException("EnrollmentReadMajorityUnavailable");
        }

        // Follower не владеет LeadershipToken; DotNext strong read подтверждает его локальный applied cut.
        using CancellationTokenSource guard = CancellationTokenSource.CreateLinkedTokenSource(token,
            raft.ConsensusToken, before.ConsensusToken);
        await raft.ApplyReadBarrierAsync(ReadBarrierType.Strong, guard.Token);
        guard.Token.ThrowIfCancellationRequested();
        ConsensusStatus? after = state.Consensus;
        if (after is null || after.IsLeader != before.IsLeader || after.Term != before.Term || after.MembershipVersion != before.MembershipVersion
            || !before.VotingNodes.SetEquals(after.VotingNodes) || after.ConsensusToken.IsCancellationRequested
            || handle.Current != raft || raft.Leader?.Id != leader.Id || after.Term != raft.AuditTrail.Term
            || !revisions.Available || !state.Accepted.CaughtUp || !state.Accepted.AccountStateConfirmed
            || !state.Accepted.SnapshotConfirmed || state.Accepted.AppliedIndex < raft.AuditTrail.LastCommittedEntryIndex
            || state.Accepted.MembershipVersion != after.MembershipVersion || !Healthy())
        {
            throw new InvalidOperationException("EnrollmentReadMajorityChanged");
        }
    }

    private bool Healthy()
    {
        return new[] { "Database", "Projection", "Storage", "Clock" }.All(name => readiness.Get(name).Status == "Ready");
    }
}
