using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

public static class DeliveryProjection
{
    public static DeliveryAttempt Attempt(RevisionImage image, AcceptedDeliveryAttempt attempt)
    {
        DeliveryAttempt record = new(attempt.Id, new JournalPosition(new ClusterIdentity(image.ClusterId, image.Generation),
            attempt.Index, attempt.Term), attempt.IntentId, attempt.Number, attempt.Authority.LeaderNodeId,
            attempt.Authority.SenderEpoch, attempt.StartedAtUtc);
        if (attempt.Outcome != AttemptOutcome.Unknown)
        {
            record.RecordResult(attempt.Outcome, attempt.CompletedAtUtc!.Value, attempt.TelegramMessageId, attempt.FailureKind);
        }

        return record;
    }

    public static DeliveryIntent Intent(RevisionImage image, MonitoringIntent intent)
    {
        ClusterIdentity cluster = new(image.ClusterId, image.Generation);
        DeliveryAttempt[] attempts = image.Monitoring.Attempts.Values.Where(item => item.IntentId == intent.Id)
            .OrderBy(item => item.Number).Select(item => Attempt(image, item)).ToArray();
        if (attempts.Length == 0 && intent.Kind != DeliveryKind.Test)
        {
            return MonitoringProjection.Intent(cluster, intent);
        }

        DeliveryIntent record = new(intent.Id, new JournalPosition(cluster, intent.Index, intent.Term), intent.IncidentId,
            intent.EventId, intent.ChatId, intent.Kind, intent.BindingGeneration, intent.LogicalKey, intent.Payload, intent.ActorUserId);
        if (attempts.Length > 0)
        {
            record.RestoreAttempts(attempts, intent.State, intent.NextAttemptAtUtc, intent.CompletedAtUtc, intent.SummaryId);
        }
        else if (intent.State == DeliveryState.Cancelled)
        {
            record.CancelNotStarted(intent.CompletedAtUtc!.Value);
        }
        else if (intent.State != DeliveryState.Planned)
        {
            throw new InvalidDataException("DeliveryAttemptHistoryMissing");
        }

        return record;
    }

    public static IReadOnlyList<ProjectionRecord> AttemptChanges(RevisionImage before, RevisionImage after)
    {
        List<ProjectionRecord> changes = [];
        foreach (AcceptedDeliveryAttempt attempt in after.Monitoring.Attempts.Values.OrderBy(item => item.Id))
        {
            if (before.Monitoring.Attempts.GetValueOrDefault(attempt.Id) != attempt)
            {
                changes.Add(Attempt(after, attempt));
            }
        }

        return changes;
    }
}
