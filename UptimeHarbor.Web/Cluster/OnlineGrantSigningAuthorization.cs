using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;

namespace UptimeHarbor.Web.Cluster;

/// <summary>Реальный majority barrier плюс accepted версии и отдельный проверенный local process source.</summary>
public class OnlineGrantSigningAuthorization(ClusterRaftHandle handle, IClusterStateView state,
    RevisionStateStore revisions, NodeReadiness readiness, IOptions<ClusterOptions> configured,
    TimeProvider time, ManagedAdmissionState? admission = null, InstallationLease? lease = null) : IOnlineGrantSigningAuthorization
{
    public async ValueTask ConfirmAsync(OnlineGrant grant, EnrollmentIssuerReference localIssuer, CancellationToken token)
    {
        OnlineGrantLocalSource.Require(grant, configured.Value, readiness, admission, lease);
        await ConfirmCoreAsync(grant, localIssuer, null, token);
        OnlineGrantLocalSource.Require(grant, configured.Value, readiness, admission, lease);
    }

    public async ValueTask ConfirmRequestAsync(OnlineGrantSigningRequest request, EnrollmentIssuerReference localIssuer,
        CancellationToken token)
    {
        await ConfirmCoreAsync(request.Grant, localIssuer, request, token);
    }

    private async ValueTask ConfirmCoreAsync(OnlineGrant grant, EnrollmentIssuerReference localIssuer,
        OnlineGrantSigningRequest? request, CancellationToken token)
    {
        var raft = handle.Current;
        ConsensusStatus? before = state.Consensus;
        var leader = raft?.Leader;
        if (raft is null || before is null || leader is null || leader.IsRemote == before.IsLeader
            || before.Term != raft.AuditTrail.Term
            || configured.Value.Compatibility != OnboardingProtocol.TRUST_COMPATIBILITY
            || localIssuer.NodeId != configured.Value.NodeId || localIssuer.StorageGeneration != configured.Value.StorageGeneration
            || !Healthy())
        {
            throw new UnauthorizedAccessException("OnlineGrantProcessUnconfirmed");
        }

        using CancellationTokenSource guard = CancellationTokenSource.CreateLinkedTokenSource(token,
            raft.ConsensusToken, before.ConsensusToken);
        await raft.ApplyReadBarrierAsync(ReadBarrierType.Strong, guard.Token);
        guard.Token.ThrowIfCancellationRequested();
        ConsensusStatus? after = state.Consensus;
        RevisionImage? image = revisions.Current;
        AcceptedEnrollmentIssuer? issuer = image?.OnboardingContracts?.Issuers?.GetValueOrDefault(localIssuer.Epoch);
        if (handle.Current != raft || after is null || after.IsLeader != before.IsLeader || after.Term != before.Term
            || raft.Leader?.Id != leader.Id || after.ConsensusToken.IsCancellationRequested
            || after.MembershipVersion != before.MembershipVersion || raft.AuditTrail.Term != after.Term
            || !revisions.Available || image is null || !state.Accepted.CaughtUp || !state.Accepted.AccountStateConfirmed
            || !state.Accepted.SnapshotConfirmed || state.Accepted.AppliedIndex < raft.AuditTrail.LastCommittedEntryIndex
            || state.Accepted.MembershipVersion != after.MembershipVersion || !Healthy()
            || grant.MembershipVersion != after.MembershipVersion
            || issuer is null || issuer.SpkiDigest != localIssuer.SpkiDigest || issuer.CertificateDigest != localIssuer.CertificateDigest
            || issuer.NodeId != localIssuer.NodeId || issuer.StorageGeneration != localIssuer.StorageGeneration
            || issuer.CertificateDer != Convert.ToBase64String(localIssuer.CertificateDer)
            || !OnlineGrantPolicy.Current(image, grant, time.GetUtcNow(), grant.Purpose == OnlineGrantPurpose.Process))
        {
            throw new UnauthorizedAccessException("OnlineGrantMajorityChanged");
        }

        EnrollmentPeerAuthority.RequireIssuer(image, grant.IssuerEpoch, time.GetUtcNow());
        if (request is not null && !OnlineGrantRequestSignatures.Verify(image, request, time.GetUtcNow()))
        {
            throw new UnauthorizedAccessException("OnlineGrantRequestProofInvalid");
        }
    }

    private bool Healthy()
    {
        return new[] { "Database", "Projection", "Storage", "Clock" }.All(name => readiness.Get(name).Status == "Ready");
    }
}
