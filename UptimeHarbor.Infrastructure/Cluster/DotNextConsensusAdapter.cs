using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Replication;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Cluster;

public class DotNextConsensusAdapter(IRaftCluster cluster, IClusterStateView state,
    NodeReadiness readiness, ClusterOptions options, RevisionStateStore? accepted = null,
    GenesisWriterPermit? genesis = null) : IConsensusWriter
{
    public async ValueTask<long> ReplicateAsync(ReadOnlyMemory<byte> payload, int commandId,
        CancellationToken cancellationToken)
    {
        if (payload.IsEmpty || payload.Length > options.CommandMaxBytes)
        {
            throw new InvalidOperationException("Cluster writer is not ready.");
        }

        if (!readiness.CanWork(state) && !CanGenesis(payload, commandId) && !CanBootstrapAccount(payload) && !CanControl(payload))
        {
            throw new InvalidOperationException("Cluster writer is not ready.");
        }

        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, cluster.LeadershipToken, cluster.ConsensusToken);
        TaskCompletionSource<long> applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BinaryLogEntry entry = new()
        {
            Content = payload,
            Term = cluster.AuditTrail.Term,
            CommandId = commandId,
            Context = applied
        };
        IReplicationCluster<IRaftLogEntry> replication = cluster;
        await replication.ReplicateAsync(entry, cancellation.Token);
        long index = await applied.Task.WaitAsync(cancellation.Token);
        await cluster.AuditTrail.WaitForApplyAsync(index, cancellation.Token);
        return index;
    }

    private bool CanGenesis(ReadOnlyMemory<byte> payload, int commandId)
    {
        ConsensusStatus? status = state.Consensus;
        if (genesis is null || accepted?.Available != true || commandId != RevisionCodec.COMMAND_ID
            || options.Compatibility != OnboardingProtocol.TRUST_COMPATIBILITY || status is null || !status.IsLeader
            || status.NodeId != options.NodeId || status.ClusterId != options.ClusterId || status.Generation != options.Generation
            || status.Term != cluster.AuditTrail.Term || status.LeadershipToken.IsCancellationRequested
            || status.ConsensusToken.IsCancellationRequested || cluster.LeadershipToken.IsCancellationRequested
            || cluster.ConsensusToken.IsCancellationRequested || status.VotingNodes.Count != 1 || !status.VotingNodes.Contains(options.NodeId)
            || status.CommittedIndex > state.Accepted.AppliedIndex || !state.Accepted.CaughtUp || !state.Accepted.SnapshotConfirmed
            || state.Accepted.MembershipVersion != status.MembershipVersion
            || new[] { "Database", "Storage", "Clock", "Projection" }.Any(name => readiness.Get(name).Status != "Ready"))
        {
            return false;
        }

        RevisionCommand command = RevisionCodec.Decode(payload.Span);
        if (command.Action == "InitializeCluster" && (command.EnrollmentTrust!.GenesisMembershipVersion != status.MembershipVersion
            || command.EnrollmentTrust.CoordinatorTerm != status.Term || !command.EnrollmentTrust.CoordinatorAtUtc.HasValue))
        {
            return false;
        }

        return genesis.Allows(command, accepted.Current!);
    }

    private bool CanControl(ReadOnlyMemory<byte> payload)
    {
        if (accepted?.Available != true)
        {
            return false;
        }

        ConsensusStatus? status = state.Consensus;
        AcceptedClusterState proof = state.Accepted;
        bool leader = status is not null && status.IsLeader && !status.LeadershipToken.IsCancellationRequested
            && !status.ConsensusToken.IsCancellationRequested;
        bool recovered = proof.CaughtUp && proof.SnapshotConfirmed && proof.AccountStateConfirmed
            && proof.AppliedIndex >= status?.CommittedIndex && proof.MembershipVersion == status?.MembershipVersion;
        bool healthy = readiness.Get("Database").Status == "Ready" && readiness.Get("Projection").Status == "Ready"
            && readiness.Get("Storage").Status == "Ready" && readiness.Get("Clock").Status == "Ready";
        if (!leader || !recovered || !healthy)
        {
            return false;
        }

        RevisionCommand command = RevisionCodec.Decode(payload.Span);
        if (OnlineLifecycleCommands.IsOnline(command))
        {
            return options.Compatibility == OnboardingProtocol.TRUST_COMPATIBILITY
                && command.OriginNodeId == options.NodeId && command.OnlineLifecycle!.Grant.MembershipVersion == status!.MembershipVersion;
        }

        return ClusterOperationValidation.IsOperation(command) && command.Operation!.MembershipVersion == status!.MembershipVersion;
    }

    private bool CanBootstrapAccount(ReadOnlyMemory<byte> payload)
    {
        if (options.Compatibility == OnboardingProtocol.TRUST_COMPATIBILITY)
        {
            // Новый online genesis использует только exact permit выше; legacy offline path не расширяется.
            return false;
        }

        if (options.Mode != "Bootstrap" || accepted?.Available != true)
        {
            return false;
        }

        RevisionImage image = accepted.Current!;
        ConsensusStatus? status = state.Consensus;
        if (status is null || !status.IsLeader || status.ClusterId != image.ClusterId || status.Generation != image.Generation
            || status.NodeId != options.NodeId || status.VotingNodes.Count != 1 || !status.VotingNodes.Contains(options.NodeId)
            || status.CommittedIndex > state.Accepted.AppliedIndex || !state.Accepted.SnapshotConfirmed || !state.Accepted.CaughtUp
            || state.Accepted.MembershipVersion != status.MembershipVersion
            || readiness.Get("Database").Status != "Ready" || readiness.Get("Storage").Status != "Ready"
            || readiness.Get("Clock").Status != "Ready" || readiness.Get("Projection").Status != "Ready")
        {
            return false;
        }

        RevisionCommand command = RevisionCodec.Decode(payload.Span);
        return command.Action == "BootstrapAccount" && command.OriginNodeId == options.NodeId
            && image.BootstrapAccountDigest == RevisionCodec.Digest(command)
            && !image.Values.Values.Any(value => value.Kind == DeletedEntityKind.Account);
    }

    public ValueTask<bool> ResignAsync(CancellationToken cancellationToken)
    {
        return cluster.ResignAsync(cancellationToken);
    }
}
