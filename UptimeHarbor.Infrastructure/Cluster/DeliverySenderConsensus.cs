using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Authority и попытки подтверждаются существующим DotNext writer и apply.</summary>
public class DeliverySenderConsensus(RevisionDispatcher dispatcher, RevisionStateStore accepted,
    IOptions<ClusterOptions> options, TimeProvider time) : ISenderConsensus
{
    public async ValueTask<AuthorityDecision?> RenewAsync(SenderAuthorityKey key, Guid roundId, CancellationToken cancellationToken)
    {
        RevisionImage image = accepted.Current ?? throw new InvalidOperationException("ProjectionUnavailable");
        DeliveryCommand payload = new() { Authority = key, RoundId = roundId };
        RevisionCommand command = DeliveryCommands.Create(image, options.Value.NodeId, options.Value.StorageGeneration,
            "RenewSender", options.Value.NodeId, roundId, time.GetUtcNow(), payload);
        CommandResult result = await dispatcher.AcceptAsync(command, options.Value.NodeId, cancellationToken);
        if (result.Outcome != OperationOutcome.Committed)
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
        CommandResult result = await dispatcher.AcceptAsync(command, options.Value.NodeId, cancellationToken);
        if (result.Outcome != OperationOutcome.Committed || !accepted.Current!.Monitoring.Attempts.ContainsKey(attemptId))
        {
            return null;
        }

        return new DeliveryAttemptPermit(key, attemptId, result.Index);
    }
}
