using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Web.Cluster;

/// <summary>Реальный majority barrier плюс accepted версии и отдельный проверенный local process source.</summary>
public class OnlineGrantSigningAuthorization(ClusterRaftHandle handle, IClusterStateView state,
    RevisionStateStore revisions, NodeReadiness readiness, IOptions<ClusterOptions> configured,
    ManagedAdmissionState admission, InstallationLease lease, TimeProvider time) : IOnlineGrantSigningAuthorization
{
    public async ValueTask ConfirmAsync(OnlineGrant grant, EnrollmentIssuerReference localIssuer, CancellationToken token)
    {
        var raft = handle.Current;
        ConsensusStatus? before = state.Consensus;
        RecoveryAdmission? local = admission.Read();
        bool process = local?.Proof switch
        {
            FreshBootstrapProof bootstrap => bootstrap.BootId == lease.BootId
                && grant.AttemptId == bootstrap.Authorization.GenesisId,
            ManagedRestartProof restart => restart.InstallIdentity == lease.InstallIdentity
                && restart.BootId == lease.BootId && restart.BootChallenge == lease.BootChallenge,
            _ => false
        };
        if (raft is null || before is null || !before.IsLeader || raft.Leader?.IsRemote != false
            || configured.Value.Compatibility != OnboardingProtocol.TRUST_COMPATIBILITY
            || !process
            || grant.BootId != lease.BootId || grant.BootChallenge != lease.BootChallenge
            || grant.NodeId != configured.Value.NodeId || grant.StorageGeneration != configured.Value.StorageGeneration
            || grant.RecoveryVersion != readiness.RecoveryVersion
            || localIssuer.NodeId != configured.Value.NodeId || localIssuer.StorageGeneration != configured.Value.StorageGeneration
            || !Healthy())
        {
            throw new UnauthorizedAccessException("OnlineGrantProcessUnconfirmed");
        }

        using CancellationTokenSource guard = CancellationTokenSource.CreateLinkedTokenSource(token,
            raft.LeadershipToken, raft.ConsensusToken, before.LeadershipToken, before.ConsensusToken);
        await raft.ApplyReadBarrierAsync(ReadBarrierType.Strong, guard.Token);
        guard.Token.ThrowIfCancellationRequested();
        ConsensusStatus? after = state.Consensus;
        RevisionImage? image = revisions.Current;
        AcceptedEnrollmentIssuer? issuer = image?.OnboardingContracts?.Issuers?.GetValueOrDefault(localIssuer.Epoch);
        if (handle.Current != raft || after is null || !after.IsLeader || after.Term != before.Term
            || after.MembershipVersion != before.MembershipVersion || raft.AuditTrail.Term != after.Term
            || !revisions.Available || image is null || !state.Accepted.CaughtUp || !state.Accepted.AccountStateConfirmed
            || !state.Accepted.SnapshotConfirmed || state.Accepted.AppliedIndex < raft.AuditTrail.LastCommittedEntryIndex
            || state.Accepted.MembershipVersion != after.MembershipVersion || !Healthy()
            || grant.RecoveryVersion != readiness.RecoveryVersion || grant.MembershipVersion != after.MembershipVersion
            || issuer is null || issuer.SpkiDigest != localIssuer.SpkiDigest || issuer.CertificateDigest != localIssuer.CertificateDigest
            || !OnlineGrantPolicy.Current(image, grant, time.GetUtcNow(), grant.Purpose == OnlineGrantPurpose.Process))
        {
            throw new UnauthorizedAccessException("OnlineGrantMajorityChanged");
        }
    }

    private bool Healthy()
    {
        return new[] { "Database", "Projection", "Storage", "Clock" }.All(name => readiness.Get(name).Status == "Ready");
    }
}
