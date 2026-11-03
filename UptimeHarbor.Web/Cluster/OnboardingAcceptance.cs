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
    IOptions<ClusterOptions> configured, IEnrollmentAuthority authority, TimeProvider time,
    RevisionStateStore? revisions = null, GenesisWriterPermit? genesis = null) : IOnboardingAcceptance
{
    public async ValueTask<RevisionCommand> PrepareAsync(RevisionCommand command, CancellationToken token)
    {
        ConsensusStatus? before = view.Consensus;
        var raft = handle.Current;
        bool leader = before is not null && before.IsLeader && !before.LeadershipToken.IsCancellationRequested
            && !before.ConsensusToken.IsCancellationRequested;
        bool healthy = new[] { "Database", "Projection", "Storage", "Clock" }
            .All(name => readiness.Get(name).Status == "Ready");
        bool initialization = command.Action == "InitializeCluster" && genesis is not null && revisions?.Available == true
            && genesis.Allows(command, revisions.Current!);
        bool recovered = view.Accepted.CaughtUp && view.Accepted.SnapshotConfirmed
            && (view.Accepted.AccountStateConfirmed || initialization);
        if (!OnboardingProtocol.Supported(configured.Value) || !leader || !healthy || !recovered || raft is null
            || raft.Leader?.IsRemote != false || raft.AuditTrail.Term != before!.Term)
        {
            throw new InvalidOperationException("OnboardingUnavailable");
        }

        bool trustCommand = EnrollmentTrustCommands.IsTrust(command);
        bool onlineCommand = OnlineLifecycleCommands.IsOnline(command);
        if (onlineCommand && (configured.Value.Compatibility != OnboardingProtocol.TRUST_COMPATIBILITY
            || command.OriginNodeId != before!.NodeId || revisions?.Available != true
            || !OnlineGrantSignatures.Verify(revisions.Current!, command.OnlineLifecycle!.Grant, time.GetUtcNow(),
                processRenewal: command.Action == "RenewOnlineProcess")))
        {
            throw new UnauthorizedAccessException("OnlineGrantUnconfirmed");
        }

        if (OnboardingCommands.IsOnboarding(command) && configured.Value.Compatibility == OnboardingProtocol.TRUST_COMPATIBILITY
            && command.Schema != 3)
        {
            throw new InvalidDataException("EnrollmentCommandSchemaUnsupported");
        }
        if (trustCommand)
        {
            if (configured.Value.Compatibility != OnboardingProtocol.TRUST_COMPATIBILITY
                || command.Action == "InitializeCluster" && (!initialization || command.OriginNodeId != before!.NodeId
                    || before.VotingNodes.Count != 1 || !before.VotingNodes.Contains(before.NodeId)))
            {
                throw new UnauthorizedAccessException("EnrollmentTrustUnavailable");
            }

            EnrollmentTrustValidation.Validate(command, time.GetUtcNow());
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

        if (onlineCommand)
        {
            OnlineGrant grant = command.OnlineLifecycle!.Grant;
            bool currentReadiness = new[] { "Database", "Projection", "Storage", "Clock" }
                .All(name => readiness.Get(name).Status == "Ready");
            if (!currentReadiness || grant.RecoveryVersion != readiness.RecoveryVersion
                || grant.MembershipVersion != after.MembershipVersion || grant.MinimumIndex > view.Accepted.AppliedIndex
                || !OnlineGrantPolicy.Current(revisions!.Current!, grant, time.GetUtcNow(),
                    registeringProcess: command.Action == "RegisterOnlineProcess", processRenewal: command.Action == "RenewOnlineProcess"))
            {
                throw new UnauthorizedAccessException("OnlineLifecycleReadinessChanged");
            }
        }

        if (trustCommand)
        {
            long nativeVersion = 0;
            if (initialization)
            {
                nativeVersion = after.MembershipVersion;
            }

            return command with { EnrollmentTrust = command.EnrollmentTrust! with
            {
                CoordinatorAtUtc = time.GetUtcNow(), CoordinatorTerm = after.Term, GenesisMembershipVersion = nativeVersion
            } };
        }

        if (onlineCommand)
        {
            return command with { OnlineLifecycle = command.OnlineLifecycle! with { CoordinatorAtUtc = time.GetUtcNow(), CoordinatorTerm = after.Term } };
        }

        return command with { Onboarding = command.Onboarding! with { CoordinatorAtUtc = time.GetUtcNow(), CoordinatorTerm = after.Term } };
    }
}
