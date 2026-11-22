using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Authority и попытки подтверждаются существующим DotNext writer и apply.</summary>
public class DeliverySenderConsensus(IRevisionTransport journal, RevisionStateStore accepted,
    IOptions<ClusterOptions> options, TimeProvider time) : ISenderConsensus
{
    public async ValueTask<AuthorityDecision?> RenewAsync(SenderAuthorityKey key, Guid roundId, CancellationToken cancellationToken)
    {
        RevisionImage image = accepted.Current ?? throw new InvalidOperationException("ProjectionUnavailable");
        DeliveryCommand payload = new() { Authority = key, RoundId = roundId };
        RevisionCommand command = DeliveryCommands.Create(image, options.Value.NodeId, options.Value.StorageGeneration,
            "RenewSender", options.Value.NodeId, roundId, time.GetUtcNow(), payload);
        CommandResult? result = await CommitAsync(command, cancellationToken);
        if (result is null)
        {
            return null;
        }

        return new AuthorityDecision(key, roundId, result.Index);
    }

    public async ValueTask<DeliveryAttemptPermit?> BeginAttemptAsync(SenderAuthorityKey key, Guid attemptId, CancellationToken cancellationToken)
    {
        RevisionImage image = accepted.Current ?? throw new InvalidOperationException("ProjectionUnavailable");
        MonitoringIntent? intent = image.Monitoring.Intents.Values.SingleOrDefault(item =>
            DeliveryCommands.AttemptId(item.Id, image.Monitoring.Attempts.Values.Count(attempt => attempt.IntentId == item.Id) + 1) == attemptId);
        if (intent is null)
        {
            return null;
        }

        RevisionCommand command = DeliveryCommands.Create(image, options.Value.NodeId, options.Value.StorageGeneration,
            "BeginDelivery", intent.Id, Guid.NewGuid(), time.GetUtcNow(), new DeliveryCommand { Authority = key, AttemptId = attemptId });
        CommandResult? result = await CommitAsync(command, cancellationToken);
        if (result is null || accepted.Current?.Monitoring.Attempts.GetValueOrDefault(attemptId)?.Authority != key)
        {
            return null;
        }

        return new DeliveryAttemptPermit(key, attemptId, result.Index);
    }

    private async Task<CommandResult?> CommitAsync(RevisionCommand command, CancellationToken token)
    {
        IReadOnlyList<CommandResult> results = await journal.SendAsync([command], token);
        PendingReplicationPump.ValidateResults([command], results);
        CommandResult decision = results.Single();
        if (decision.Outcome != OperationOutcome.Committed)
        {
            return null;
        }

        // Ответ лидера может прийти до локального apply на выбранном узле.
        TaskCompletionSource applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            if (!accepted.Available || accepted.Current!.Index >= decision.Index)
            {
                applied.TrySetResult();
            }
        }

        accepted.Changed += Check;
        try
        {
            Check();
            await applied.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
            if (accepted.Available && accepted.Current!.Results.GetValueOrDefault(command.OperationId) == decision)
            {
                return decision;
            }

            return null;
        }
        finally
        {
            accepted.Changed -= Check;
        }
    }
}
