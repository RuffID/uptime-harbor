using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

/// <summary>Свежий срез принимающего лидера и проверка его неизменности до ответа.</summary>
internal sealed class VoterContinuationAudience(RevisionStateStore revisions, IEnrollmentReadBarrier barrier,
    IClusterStateView cluster, IOptions<ClusterOptions> options, InstallationState installation)
{
    internal async Task<Cut> ReadAsync(CancellationToken token)
    {
        ClusterOptions local = options.Value;
        LocalInstallationProfile profile = installation.Current ?? throw new UnauthorizedAccessException("VoterContinuationAudienceUnavailable");
        ConsensusStatus leader = cluster.Consensus ?? throw new UnauthorizedAccessException("VoterContinuationLeaderRequired");
        long recovery = revisions.RecoveryVersion;
        if (local.Compatibility != OnboardingProtocol.TRUST_COMPATIBILITY || !local.Enabled || !installation.CommonOpen
            || profile.Phase != InstallationPhase.ClusterReady || profile.NodeId != local.NodeId
            || profile.StorageGeneration != local.StorageGeneration || profile.ClusterId != local.ClusterId
            || profile.ClusterGeneration != local.Generation || profile.PeerAddress != local.PublicUri
            || !leader.IsLeader || leader.NodeId != local.NodeId)
        {
            throw new UnauthorizedAccessException("VoterContinuationAudienceUnavailable");
        }

        await barrier.ConfirmAsync(token);
        RevisionImage image = revisions.Current ?? throw new UnauthorizedAccessException("VoterContinuationProjectionUnavailable");
        OperationalNode audience = EnrollmentPeerAuthority.RequireVoter(image, local.NodeId, local.StorageGeneration);
        if (audience.Address != local.PublicUri)
        {
            throw new UnauthorizedAccessException("VoterContinuationAudienceUnavailable");
        }

        Cut cut = new(image, recovery, leader, profile);
        RequireUnchanged(cut, image, token);
        return cut;
    }

    internal void RequireUnchanged(Cut cut, RevisionImage image, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RuntimeGenerationContext.Verify();
        ConsensusStatus? current = cluster.Consensus;
        AcceptedClusterState proof = cluster.Accepted;
        ConsensusStatus leader = cut.Leader;
        if (current is not { IsLeader: true } || current.NodeId != leader.NodeId || current.Term <= 0
            || current.Term != leader.Term || current.Term < image.Term || current.ClusterId != image.ClusterId
            || current.Generation != image.Generation || current.MembershipVersion != leader.MembershipVersion
            || current.MembershipVersion != image.Operations.MembershipVersion || current.CommittedIndex > image.Index
            || !current.VotingNodes.SetEquals(leader.VotingNodes) || !current.VotingNodes.Contains(current.NodeId)
            || !current.VotingNodes.SetEquals(image.Operations.Nodes.Values.Where(node => node.Voting && !node.Removed).Select(node => node.NodeId))
            || current.ConsensusToken != leader.ConsensusToken || current.LeadershipToken != leader.LeadershipToken
            || current.ConsensusToken.IsCancellationRequested || current.LeadershipToken.IsCancellationRequested
            || !proof.AccountStateConfirmed || !proof.SnapshotConfirmed || !proof.CaughtUp || !proof.Activated
            || !proof.ManuallyEnabled || proof.MembershipChangePending
            || proof.AppliedIndex != image.Index || proof.MembershipVersion != current.MembershipVersion
            || !ReferenceEquals(installation.Current, cut.Profile) || !installation.CommonOpen
            || !revisions.TryAtCurrent(image, cut.Recovery, static () => { }))
        {
            throw new UnauthorizedAccessException("VoterContinuationAuthorityChanged");
        }
    }

    internal sealed record Cut(RevisionImage Image, long Recovery, ConsensusStatus Leader, LocalInstallationProfile Profile);
}
