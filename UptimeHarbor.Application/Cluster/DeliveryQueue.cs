using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

/// <summary>Очередь полностью выводится из принятой истории, локальный outbox не требуется.</summary>
public static class DeliveryQueue
{
    public static IReadOnlyList<MonitoringIntent> Due(RevisionImage image, DateTimeOffset atUtc, int limit)
    {
        return image.Monitoring.Intents.Values.Where(intent => intent.State is DeliveryState.Planned or DeliveryState.RetryDue
            && (!intent.NextAttemptAtUtc.HasValue || intent.NextAttemptAtUtc <= atUtc) && DeliveryEligibility.Allows(image, intent)
            && !NeedsBacklogPlan(image, intent))
            .OrderBy(intent => intent.Index).ThenBy(intent => intent.Id).Take(limit).ToArray();
    }

    private static bool NeedsBacklogPlan(RevisionImage image, MonitoringIntent intent)
    {
        if (!intent.IncidentId.HasValue || intent.Kind == DeliveryKind.Recovery)
        {
            return false;
        }

        MonitoringIncident incident = image.Monitoring.Incidents[intent.IncidentId.Value];
        return incident.Completion == IncidentCompletion.Recovered
            && !MonitoringRecipients.HasConfirmedProblem(image, incident.Id, intent.ChatId);
    }

    public static void Validate(RevisionImage image)
    {
        foreach (AcceptedDeliveryAttempt attempt in image.Monitoring.Attempts.Values)
        {
            SenderAuthorityKey key = attempt.Authority;
            bool currentIdentity = key.ClusterId == image.ClusterId && key.Generation == image.Generation;
            bool selectedHistory = IsSelectedHistory(image.RecoveryBaseline, attempt);
            if ((!currentIdentity && !selectedHistory) || key.Term <= 0
                || key.Term > attempt.Term || key.MembershipVersion < 0
                || attempt.Outcome == AttemptOutcome.Unknown && (attempt.CompletedAtUtc.HasValue
                    || attempt.TelegramMessageId.HasValue || attempt.FailureKind is not null))
            {
                throw new InvalidDataException("DeliveryAttemptAuthorityMismatch");
            }

            if (attempt.Outcome != AttemptOutcome.Unknown)
            {
                DeliveryRetry.ValidateFailure(attempt.FailureKind);
                bool succeeded = attempt.Outcome == AttemptOutcome.Succeeded;
                if (succeeded != (attempt.FailureKind is null) || attempt.TelegramMessageId <= 0)
                {
                    throw new InvalidDataException("DeliveryAttemptResultMismatch");
                }
            }
        }

        foreach (MonitoringIntent intent in image.Monitoring.Intents.Values)
        {
            DeliveryIntent record = DeliveryProjection.Intent(image, intent);
            if (intent.State == DeliveryState.Succeeded)
            {
                MonitoringDeliveryEvidence proof = image.Monitoring.ConfirmedDeliveries.GetValueOrDefault(intent.Id)
                    ?? throw new InvalidDataException("DeliveryEvidenceMissing");
                AcceptedDeliveryAttempt attempt = image.Monitoring.Attempts.GetValueOrDefault(proof.AttemptId)
                    ?? throw new InvalidDataException("DeliveryAttemptHistoryMissing");
                bool matches = attempt.IntentId == intent.Id && attempt.Outcome == AttemptOutcome.Succeeded
                    && attempt.Number == proof.AttemptNumber && attempt.Authority.LeaderNodeId == proof.SenderNodeId
                    && attempt.Authority.SenderEpoch == proof.SenderEpoch && attempt.StartedAtUtc == proof.StartedAtUtc
                    && attempt.CompletedAtUtc == proof.SucceededAtUtc && attempt.TelegramMessageId == proof.TelegramMessageId
                    && record.CompletedAtUtc == proof.SucceededAtUtc && attempt.Index == proof.AcceptedIndex && attempt.Term == proof.AcceptedTerm;
                if (!matches)
                {
                    throw new InvalidDataException("DeliveryEvidenceMismatch");
                }
            }
        }

        if (image.Monitoring.Attempts.Values.Any(attempt => !image.Monitoring.Intents.ContainsKey(attempt.IntentId)
            || attempt.Id != DeliveryCommands.AttemptId(attempt.IntentId, attempt.Number)
            || attempt.Index > image.Index || attempt.Term > image.Term)
            || image.Monitoring.ConfirmedDeliveries.Keys.Any(id => image.Monitoring.Intents[id].State != DeliveryState.Succeeded))
        {
            throw new InvalidDataException("DeliveryHistoryMismatch");
        }
    }

    private static bool IsSelectedHistory(RecoveryBaseline? baseline, AcceptedDeliveryAttempt attempt)
    {
        if (baseline is null)
        {
            return false;
        }

        RecoveredAttemptIdentity? original = baseline.Attempts.GetValueOrDefault(attempt.Id);
        if (original is not null && original.IntentId == attempt.IntentId && original.Number == attempt.Number
            && original.Authority == attempt.Authority && original.StartedAtUtc == attempt.StartedAtUtc)
        {
            return true;
        }

        return false;
    }
}
