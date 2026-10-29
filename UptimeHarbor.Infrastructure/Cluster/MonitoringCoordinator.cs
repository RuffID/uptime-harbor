using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>История до live head, только после recovery. Один проход ограничен числом accepted commands.</summary>
public class MonitoringCoordinator(RevisionStateStore accepted, IClusterStateView state, NodeReadiness readiness,
    RevisionDispatcher dispatcher, IOptions<ClusterOptions> options, TimeProvider time)
{
    private Guid afterTarget;

    public async Task RunOnceAsync(CancellationToken token)
    {
        ConsensusStatus? status = state.Consensus;
        if (!options.Value.Enabled || !accepted.Available || !readiness.CanWork(state)
            || status?.IsLeader != true || status.LeadershipToken.IsCancellationRequested || status.ConsensusToken.IsCancellationRequested)
        {
            return;
        }

        RevisionImage image = accepted.Current!;
        int budget = 100;
        Guid[] targets = image.Values.Values.Where(cell => !cell.Deleted && cell.Value.Enabled
            && cell.Kind is DeletedEntityKind.Server or DeletedEntityKind.Container).Select(cell => cell.Id)
            .Concat(image.Monitoring.Nodes.Keys).Distinct().OrderBy(id => id.CompareTo(afterTarget) <= 0).ThenBy(id => id).ToArray();
        foreach (Guid id in targets)
        {
            image = accepted.Current!;
            MonitoringTarget? target = MonitoringTargets.Find(image, id);
            if (target is null || target.Observers.IsEmpty)
            {
                continue;
            }

            DateTimeOffset? watermark = MonitoringReducer.HistoricalWatermark(image, target);
            MonitoringEvaluation? history = image.Monitoring.History.GetValueOrDefault(id);
            DateTimeOffset after = target.AssignedAtUtc;
            if (history is not null && history.CheckGeneration == target.CheckGeneration)
            {
                after = history.AtUtc;
            }

            DateTimeOffset[] pendingTimes = image.Monitoring.Observations.Values.Where(item => item.TargetId == id
                && item.CheckGeneration == target.CheckGeneration && item.ClockConfirmed
                && item.CheckedAtUtc > after && item.CheckedAtUtc <= watermark)
                .Select(item => item.CheckedAtUtc).Distinct().Order().Take(Math.Min(10, budget) + 1).ToArray();
            int quota = Math.Min(10, budget);
            foreach (DateTimeOffset at in pendingTimes.Take(quota))
            {
                image = accepted.Current!;
                CommandResult result = await dispatcher.AcceptAsync(MonitoringCommands.Evaluate(image, target, options.Value.NodeId,
                    options.Value.StorageGeneration, status.Term, at, true, time.GetUtcNow()), options.Value.NodeId, token);
                if (result.Outcome != OperationOutcome.Committed)
                {
                    return;
                }

                budget--;
            }

            afterTarget = id;
            if (budget == 0)
            {
                return;
            }

            if (pendingTimes.Length > quota)
            {
                continue;
            }

            image = accepted.Current!;
            DateTimeOffset now = time.GetUtcNow();
            MonitoringEvaluation? live = image.Monitoring.Live.GetValueOrDefault(id);
            if (live is not null && live.CheckGeneration == target.CheckGeneration
                && live.FrontierDigest == MonitoringTargets.Frontier(image, target)
                && now - live.AtUtc < TimeSpan.FromSeconds(5))
            {
                continue;
            }

            await dispatcher.AcceptAsync(MonitoringCommands.Evaluate(image, target, options.Value.NodeId,
                options.Value.StorageGeneration, status.Term, now, false, now), options.Value.NodeId, token);
            budget--;
            if (budget == 0)
            {
                return;
            }
        }

        // До сверки принятой delivery history никакая старая очередь не получает новый план.
        image = accepted.Current!;
        bool historyPending = targets.Any(id =>
        {
            MonitoringTarget? target = MonitoringTargets.Find(image, id);
            if (target is null || target.Observers.IsEmpty)
            {
                return false;
            }

            DateTimeOffset? watermark = MonitoringReducer.HistoricalWatermark(image, target);
            MonitoringEvaluation? history = image.Monitoring.History.GetValueOrDefault(id);
            return !watermark.HasValue || history is null || history.CheckGeneration != target.CheckGeneration || history.AtUtc < watermark;
        });
        bool completedBacklog = image.Monitoring.Intents.Values.Any(intent => intent.IncidentId.HasValue
            && intent.State is DeliveryState.Planned or DeliveryState.RetryDue
            && image.Monitoring.Incidents[intent.IncidentId.Value].Completion == IncidentCompletion.Recovered
            && !MonitoringRecipients.HasConfirmedProblem(image, intent.IncidentId.Value, intent.ChatId));
        if (!historyPending && completedBacklog && state.Accepted.DeliveryStateConfirmed && budget > 0)
        {
            image = accepted.Current!;
            RevisionCommand command = new()
            {
                ClusterId = image.ClusterId, ClusterGeneration = image.Generation, OperationId = MonitoringKeys.Id($"backlog/{image.ClusterId:N}/{status.Term}/{image.Index}"),
                OriginNodeId = options.Value.NodeId, ActorUserId = Guid.Empty, ActorRevision = 0,
                EntityId = options.Value.NodeId, EntityKind = DeletedEntityKind.Node, Action = "PlanBacklogDelivery",
                BaseRevision = 0, CreatedAtUtc = time.GetUtcNow(), SystemSource = "MonitoringCoordinator",
                Monitoring = new MonitoringCommand { StorageGeneration = options.Value.StorageGeneration, CoordinatorTerm = status.Term, CutIndex = image.Index }
            };
            await dispatcher.AcceptAsync(command, options.Value.NodeId, token);
        }
    }
}
