using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Web.Cluster;

/// <summary>Время принимающего coordinator фиксируется в WAL после majority barrier.</summary>
public class OnboardingAcceptance(ClusterRaftHandle handle, IClusterStateView view, NodeReadiness readiness,
    IOptions<ClusterOptions> configured, IEnrollmentAuthority authority, TimeProvider time) : IOnboardingAcceptance
{
    public async ValueTask<RevisionCommand> PrepareAsync(RevisionCommand command, CancellationToken token)
    {
        ConsensusStatus? before = view.Consensus;
        var raft = handle.Current;
        bool leader = before is not null && before.IsLeader && !before.LeadershipToken.IsCancellationRequested
            && !before.ConsensusToken.IsCancellationRequested;
        bool healthy = new[] { "Database", "Projection", "Storage", "Clock" }
            .All(name => readiness.Get(name).Status == "Ready");
        bool recovered = view.Accepted.CaughtUp && view.Accepted.SnapshotConfirmed && view.Accepted.AccountStateConfirmed;
        if (!OnboardingProtocol.Supported(configured.Value) || !leader || !healthy || !recovered || raft is null
            || raft.Leader?.IsRemote != false || raft.AuditTrail.Term != before!.Term)
        {
            throw new InvalidOperationException("OnboardingUnavailable");
        }

        if (OnboardingCommands.IsCoordinator(command) && command.OriginNodeId != before!.NodeId)
        {
            throw new UnauthorizedAccessException("OnboardingCoordinatorRequired");
        }

        if (OnboardingCommands.NeedsEnrollmentAuthority(command) && !await authority.VerifyAsync(command, token))
        {
            throw new UnauthorizedAccessException("EnrollmentAuthorityUnavailable");
        }

        using CancellationTokenSource guard = CancellationTokenSource.CreateLinkedTokenSource(token,
            raft.LeadershipToken, raft.ConsensusToken);
        await raft.ApplyReadBarrierAsync(ReadBarrierType.Strong, guard.Token);
        guard.Token.ThrowIfCancellationRequested();
        ConsensusStatus? after = view.Consensus;
        if (after is null || !after.IsLeader || after.Term != before!.Term || after.MembershipVersion != before.MembershipVersion
            || after.LeadershipToken.IsCancellationRequested || after.ConsensusToken.IsCancellationRequested || !view.Accepted.CaughtUp
            || handle.Current != raft || raft.Leader?.IsRemote != false || raft.AuditTrail.Term != after.Term
            || view.Accepted.AppliedIndex < raft.AuditTrail.LastCommittedEntryIndex
            || view.Accepted.MembershipVersion != after.MembershipVersion)
        {
            throw new InvalidOperationException("OnboardingCoordinatorChanged");
        }

        return command with { Onboarding = command.Onboarding! with { CoordinatorAtUtc = time.GetUtcNow(), CoordinatorTerm = after.Term } };
    }
}
