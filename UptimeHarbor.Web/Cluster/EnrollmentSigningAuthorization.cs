using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Web.Cluster;

/// <summary>Не зависит от runtime/dispatcher; native strong barrier подтверждает текущую accepted выдачу.</summary>
public class EnrollmentSigningAuthorization(ClusterRaftHandle handle, IClusterStateView view, RevisionStateStore accepted,
    NodeReadiness readiness, IOptions<ClusterOptions> options, TimeProvider time) : IEnrollmentSigningAuthorization
{
    public async ValueTask<long> ConfirmAsync(EnrollmentRequest request, EnrollmentIssuance issuance,
        EnrollmentIssuerReference localIssuer, CancellationToken token)
    {
        var raft = handle.Current;
        ConsensusStatus? before = view.Consensus;
        var leader = raft?.Leader;
        if (raft is null || before is null || leader is null
            || options.Value.Compatibility != OnboardingProtocol.TRUST_COMPATIBILITY
            || before.Term != raft.AuditTrail.Term || !Healthy())
        {
            throw new InvalidOperationException("EnrollmentSigningMajorityUnavailable");
        }

        // DotNext гарантирует linearizable read и для follower; тип Strong задаёт поведение leader.
        // Issuer не обязан быть leader, а его LeadershipToken на follower не является правом подписи.
        using CancellationTokenSource guard = CancellationTokenSource.CreateLinkedTokenSource(token,
            raft.ConsensusToken, before.ConsensusToken);
        await raft.ApplyReadBarrierAsync(ReadBarrierType.Strong, guard.Token);
        guard.Token.ThrowIfCancellationRequested();
        ConsensusStatus? after = view.Consensus;
        RevisionImage? image = accepted.Current;
        if (after is null || before.IsLeader != after.IsLeader || before.Term != after.Term || before.MembershipVersion != after.MembershipVersion
            || handle.Current != raft || raft.Leader?.Id != leader.Id || raft.AuditTrail.Term != after.Term
            || !accepted.Available || image is null || !Healthy() || !view.Accepted.AccountStateConfirmed
            || !view.Accepted.CaughtUp || !view.Accepted.SnapshotConfirmed
            || view.Accepted.AppliedIndex < raft.AuditTrail.LastCommittedEntryIndex
            || view.Accepted.MembershipVersion != after.MembershipVersion || image.Operations.MaintenanceId != Guid.Empty
            || image.ClusterId != request.ClusterId || image.Generation != request.Generation)
        {
            throw new InvalidOperationException("EnrollmentSigningMajorityChanged");
        }

        return EnrollmentSigningPolicy.Require(image, request, issuance, localIssuer, after.NodeId,
            options.Value.StorageGeneration, time.GetUtcNow());
    }

    private bool Healthy()
    {
        return new[] { "Database", "Projection", "Storage", "Clock" }.All(name => readiness.Get(name).Status == "Ready");
    }
}
