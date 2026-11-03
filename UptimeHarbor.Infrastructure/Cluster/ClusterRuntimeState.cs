using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using Microsoft.Extensions.Options;

namespace UptimeHarbor.Infrastructure.Cluster;

public class ClusterRuntimeState(IClusterRecovery recovery, RevisionStateStore? revisions = null,
    ClusterLocalControl? control = null, IOptions<ClusterOptions>? options = null, TimeProvider? time = null) : IClusterStateView
{
    private ConsensusStatus? status;
    public ConsensusStatus? Consensus => Volatile.Read(ref status);
    public AcceptedClusterState Accepted
    {
        get
        {
            AcceptedClusterState proof = recovery.Accepted;
            RevisionImage? image = revisions?.Current;
            ConsensusStatus? current = Consensus;
            if (control is null || options is null || revisions?.Available != true || image is null || current is null
                || !proof.CaughtUp || !proof.SnapshotConfirmed || !proof.AccountStateConfirmed)
            {
                return proof;
            }

            ClusterOperationsImage operations = image.Operations;
            OperationalNode? local = operations.Nodes.GetValueOrDefault(options.Value.NodeId);
            OperationalCredential? credential = operations.Credentials.GetValueOrDefault(options.Value.CredentialId);
            bool trusted = local is not null && local.StorageGeneration == options.Value.StorageGeneration
                && local.Address == options.Value.PublicUri && !local.Removed && credential is not null
                && credential.NodeId == local.NodeId && !credential.RevokedAtUtc.HasValue
                && credential.ExpiresAtUtc > (time ?? TimeProvider.System).GetUtcNow();
            HashSet<Guid> voters = operations.Nodes.Values.Where(node => node.Voting && !node.Removed)
                .Select(node => node.NodeId).ToHashSet();
            bool membership = voters.SetEquals(current.VotingNodes) && operations.MembershipVersion == current.MembershipVersion;
            MembershipOperation? transition = operations.Changes.Values.SingleOrDefault(change => !change.ConfirmedVersion.HasValue);
            if (transition is not null && current.MembershipVersion > transition.ExpectedVersion)
            {
                HashSet<Guid> transitioning = new(voters);
                if (transition.Remove)
                {
                    transitioning.Remove(transition.NodeId);
                }
                else
                {
                    transitioning.Add(transition.NodeId);
                }

                // Сохраняет доступ прежних активированных членов к reauthorization,
                // только когда native результат точно соответствует принятому intent.
                membership = transitioning.SetEquals(current.VotingNodes);
            }

            trusted = trusted && membership && operations.BotId == options.Value.ExpectedBotId;
            bool activated = trusted && local!.Voting && local.Activated && operations.MaintenanceId == Guid.Empty;
            bool manual = activated && control.CanWork(operations.Revision, operations.MaintenanceId);
            bool pending = operations.Changes.Values.Any(change => !change.ConfirmedVersion.HasValue);
            SenderAuthorityKey? sender = operations.Sender;
            Guid epoch = Guid.Empty;
            if (sender is not null && sender.ClusterId == current.ClusterId && sender.Generation == current.Generation
                && sender.Term == current.Term && sender.MembershipVersion == current.MembershipVersion
                && voters.Contains(sender.LeaderNodeId) && operations.MaintenanceId == Guid.Empty && !pending)
            {
                epoch = sender.SenderEpoch;
            }

            bool delivery = false;
            if (activated && proof.AppliedIndex >= current.CommittedIndex)
            {
                DeliveryQueue.Validate(image);
                delivery = true;
            }

            return proof with { Activated = activated, ManuallyEnabled = manual, SenderEpoch = epoch,
                DeliveryStateConfirmed = delivery, MembershipChangePending = pending };
        }
    }

    public void Publish(ConsensusStatus? value)
    {
        if (value is not null)
        {
            UptimeHarbor.Application.Installation.RuntimeGenerationContext.Verify();
        }

        Volatile.Write(ref status, value);
    }
}
