using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Web.Cluster;

/// <summary>Собирает готовые recovery/membership/activation механизмы, не запускает рабочие роли по одному replay.</summary>
public class ClusterLifecycleWorker(ClusterPeerRuntime runtime, ClusterRuntimeState state, RevisionStateStore revisions,
    ClusterLocalControl control, ClusterHealthWorker health, RevisionDispatcher dispatcher, SenderAuthorityGate gate,
    IServiceScopeFactory scopes, IOptions<ClusterOptions> options, TimeProvider time,
    ILogger<ClusterLifecycleWorker> logger,
    IOptions<UptimeHarbor.Infrastructure.Installation.InstallationOptions>? installation = null)
    : UptimeHarbor.Infrastructure.Installation.RestartableWorker
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                gate.Close();
                logger.LogWarning("Cluster lifecycle reconciliation is unconfirmed for {NodeId}.", options.Value.NodeId);
            }
        }
    }

    public async Task TickAsync(CancellationToken token)
    {
        ConsensusStatus? status = state.Consensus;
        RevisionImage? image = revisions.Current;
        if (!revisions.Available || image is null || status is null || !status.IsLeader
            || status.ConsensusToken.IsCancellationRequested || status.LeadershipToken.IsCancellationRequested
            || !state.Accepted.AccountStateConfirmed || !state.Accepted.CaughtUp || runtime.Raft is null)
        {
            return;
        }

        await runtime.Raft.ApplyReadBarrierAsync(ReadBarrierType.Strong, token);
        await runtime.RefreshStatusAsync(token);
        ConsensusStatus? verified = state.Consensus;
        if (verified is null || !verified.IsLeader || verified.Term != status.Term
            || verified.MembershipVersion != status.MembershipVersion || !state.Accepted.CaughtUp)
        {
            return;
        }

        image = revisions.Current!;
        Guid maintenance = image.Operations.MaintenanceId;
        if (maintenance != Guid.Empty)
        {
            if (control.CanLeaveMaintenance(maintenance))
            {
                await SubmitAsync(image, status, "LeaveMaintenance", status.NodeId,
                    new ClusterOperation { MaintenanceId = maintenance }, token);
            }

            return;
        }

        MembershipOperation? pending = image.Operations.Changes.Values.SingleOrDefault(change => !change.ConfirmedVersion.HasValue);
        if (pending is not null)
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<MembershipCoordinator>()
                .ExecuteAcceptedAsync(pending.ActorUserId, pending.Id, token);
            return;
        }

        foreach (OperationalNode node in image.Operations.Nodes.Values.Where(node => node.Voting && !node.Removed && !node.Activated))
        {
            bool requested = false;
            if (node.NodeId == status.NodeId)
            {
                requested = control.ActivationRequested(image.Operations.Revision);
            }
            else if (health.Peers.TryGetValue(node.NodeId, out ObservedPeerHealth? peer))
            {
                bool fresh = peer.Age >= TimeSpan.Zero && peer.Age < TimeSpan.FromSeconds(options.Value.HealthFreshSeconds);
                bool caughtUp = peer.Health.Term == status.Term && peer.Health.MembershipVersion == status.MembershipVersion
                    && peer.Health.AppliedIndex >= status.CommittedIndex;
                bool healthy = new[] { "Database", "Projection", "Storage", "Clock" }.All(component =>
                    peer.Health.Components.TryGetValue(component, out HealthResult? result) && result.Status == "Ready"
                    && result.Age >= TimeSpan.Zero && result.Age + peer.Age < TimeSpan.FromSeconds(10));
                requested = fresh && caughtUp && healthy && peer.Health.ActivationRequested;
            }

            if (requested)
            {
                await SubmitAsync(image, status, "ActivateNode", node.NodeId, new ClusterOperation(), token);
                return;
            }
        }

        if (!state.Accepted.Activated || !state.Accepted.ManuallyEnabled || !state.Accepted.DeliveryStateConfirmed)
        {
            return;
        }

        if (installation?.Value.Setup == true)
        {
            return;
        }

        SenderAuthorityKey? sender = image.Operations.Sender;
        if (sender is null || sender.Term != status.Term || sender.MembershipVersion != status.MembershipVersion
            || sender.LeaderNodeId != status.NodeId)
        {
            SenderAuthorityKey key = new(status.ClusterId, status.Generation, status.MembershipVersion, status.Term,
                status.NodeId, Guid.NewGuid());
            await SubmitAsync(image, status, "OpenSenderEpoch", status.NodeId, new ClusterOperation { Sender = key }, token);
        }

        gate.ConfirmMembershipReconciled();
    }

    private async Task SubmitAsync(RevisionImage image, ConsensusStatus status, string action, Guid nodeId,
        ClusterOperation operation, CancellationToken token)
    {
        RevisionCommand command = ClusterLifecycleCommands.Create(image, options.Value.NodeId, options.Value.StorageGeneration,
            action, nodeId, operation with { MembershipVersion = status.MembershipVersion }, time.GetUtcNow());
        CommandResult result = await dispatcher.AcceptAsync(command, options.Value.NodeId, token);
        logger.LogInformation("Cluster lifecycle {Action} for {NodeId}: {Outcome} at {Index}.",
            action, nodeId, result.Outcome, result.Index);
    }
}
