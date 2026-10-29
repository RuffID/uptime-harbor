using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

/// <summary>Attempts/results решаются тем же reducer и сохраняются с его receipt/cursor.</summary>
public static class DeliveryReducer
{
    public static RevisionImage Apply(RevisionImage image, RevisionCommand command)
    {
        DeliveryCommand payload = command.Delivery!;
        if (!image.Monitoring.Nodes.TryGetValue(command.OriginNodeId, out Guid storage) || storage != payload.StorageGeneration)
        {
            return Decide(image, command, "SenderNotAdmitted");
        }

        if (command.Action == "RecordDelivery")
        {
            return Result(image, command, false);
        }

        if (payload.Authority!.Term != image.Term)
        {
            return Decide(image, command, "SenderTermChanged");
        }

        if (command.Action == "RenewSender")
        {
            return Decide(image, command, null);
        }

        if (command.Action == "AbandonDelivery")
        {
            return Result(image, command, true);
        }

        MonitoringIntent? intent = image.Monitoring.Intents.GetValueOrDefault(command.EntityId);
        int number = image.Monitoring.Attempts.Values.Count(item => item.IntentId == command.EntityId) + 1;
        bool due = intent is not null && intent.State is DeliveryState.Planned or DeliveryState.RetryDue
            && (!intent.NextAttemptAtUtc.HasValue || intent.NextAttemptAtUtc <= command.CreatedAtUtc);
        if (!due || number > 5 || payload.AttemptId != DeliveryCommands.AttemptId(command.EntityId, number)
            || !DeliveryEligibility.Allows(image, intent!))
        {
            return Decide(image, command, "DeliveryNotDueOrCancelled");
        }

        AcceptedDeliveryAttempt attempt = new(payload.AttemptId, intent!.Id, number, payload.Authority,
            command.CreatedAtUtc, image.Index, image.Term);
        intent = intent with { State = DeliveryState.AttemptStarted, NextAttemptAtUtc = null, Index = image.Index, Term = image.Term };
        image = image with { Monitoring = image.Monitoring with
        {
            Attempts = image.Monitoring.Attempts.Add(attempt.Id, attempt),
            Intents = image.Monitoring.Intents.SetItem(intent.Id, intent)
        } };
        return Decide(image, command, null);
    }

    private static RevisionImage Result(RevisionImage image, RevisionCommand command, bool abandoned)
    {
        DeliveryCommand payload = command.Delivery!;
        AcceptedDeliveryAttempt? attempt = image.Monitoring.Attempts.GetValueOrDefault(payload.AttemptId);
        if (attempt is null || command.EntityId != attempt.IntentId)
        {
            return Decide(image, command, "AttemptMissing");
        }

        if (!abandoned && attempt.Authority.LeaderNodeId != command.OriginNodeId)
        {
            return Decide(image, command, "ResultOriginMismatch");
        }

        DateTimeOffset completedAt = payload.CompletedAtUtc ?? command.CreatedAtUtc;
        if (completedAt < attempt.StartedAtUtc)
        {
            return Decide(image, command, "ResultClockInvalid");
        }

        if (abandoned && (attempt.Outcome != AttemptOutcome.Unknown
            || command.CreatedAtUtc - attempt.StartedAtUtc < TimeSpan.FromSeconds(30)))
        {
            return Decide(image, command, "AttemptStillInFlight");
        }

        AttemptOutcome outcome = payload.Outcome;
        string? failure = payload.FailureKind;
        if (abandoned)
        {
            outcome = AttemptOutcome.Failed;
            failure = "Ambiguous";
        }

        if (attempt.Outcome == AttemptOutcome.Succeeded || attempt.Outcome != AttemptOutcome.Unknown
            && outcome != AttemptOutcome.Succeeded)
        {
            return Decide(image, command, null);
        }

        attempt = attempt with { Outcome = outcome, CompletedAtUtc = completedAt,
            TelegramMessageId = payload.TelegramMessageId, FailureKind = failure, Index = image.Index, Term = image.Term };
        MonitoringIntent intent = image.Monitoring.Intents[attempt.IntentId];
        MonitoringImage monitoring = image.Monitoring with { Attempts = image.Monitoring.Attempts.SetItem(attempt.Id, attempt) };
        image = image with { Monitoring = monitoring };
        if (outcome == AttemptOutcome.Succeeded)
        {
            if (intent.State != DeliveryState.Succeeded)
            {
                intent = intent with { State = DeliveryState.Succeeded, CompletedAtUtc = completedAt,
                    NextAttemptAtUtc = null, Index = image.Index, Term = image.Term };
                MonitoringDeliveryEvidence evidence = new(intent.Id, attempt.Id, image.Index, image.Term, attempt.Number,
                    attempt.Authority.LeaderNodeId, attempt.Authority.SenderEpoch, attempt.StartedAtUtc,
                    completedAt, attempt.TelegramMessageId!.Value);
                monitoring = monitoring with { ConfirmedDeliveries = monitoring.ConfirmedDeliveries.Add(intent.Id, evidence) };
            }
        }
        else if (intent.State == DeliveryState.AttemptStarted
            && attempt.Number == monitoring.Attempts.Values.Count(item => item.IntentId == intent.Id))
        {
            DateTimeOffset? next = DeliveryRetry.Next(attempt.Number, outcome, failure, completedAt);
            DeliveryState state = DeliveryState.Failed;
            DateTimeOffset? terminalAt = completedAt;
            if (!DeliveryEligibility.Allows(image, intent))
            {
                state = DeliveryState.Cancelled;
                next = null;
            }
            else if (next.HasValue)
            {
                state = DeliveryState.RetryDue;
                terminalAt = null;
            }

            intent = intent with { State = state, NextAttemptAtUtc = next, CompletedAtUtc = terminalAt,
                Index = image.Index, Term = image.Term };
        }

        image = image with { Monitoring = monitoring with { Intents = monitoring.Intents.SetItem(intent.Id, intent) } };
        if (intent.Kind == DeliveryKind.Summary && outcome == AttemptOutcome.CancelledBeforeTransport)
        {
            image = MonitoringBacklog.RevokeUnstarted(image, source => !DeliveryEligibility.Allows(image, source), completedAt);
        }

        if (outcome == AttemptOutcome.Succeeded && intent.IncidentId.HasValue
            && intent.Kind is DeliveryKind.Problem or DeliveryKind.Update)
        {
            MonitoringIncident incident = image.Monitoring.Incidents[intent.IncidentId.Value];
            if (incident.Completion == IncidentCompletion.Recovered && DeliveryEligibility.Allows(image, intent with { Kind = DeliveryKind.Recovery }))
            {
                MonitoringEvent recovery = image.Monitoring.Events.Values.Single(item => item.IncidentId == incident.Id && item.Kind == "Recovered");
                image = MonitoringRecipients.Add(image, incident, recovery, intent.ChatId, intent.BindingGeneration, DeliveryKind.Recovery);
            }
        }

        return Decide(image, command, null);
    }

    public static RevisionImage PlanTest(RevisionImage image, RevisionCommand command)
    {
        if (!MonitoringRecipients.ChatExists(image, command.EntityId))
        {
            return Decide(image, command, "ChatDeletedOrMissing");
        }

        Guid id = command.OperationId;
        MonitoringIntent intent = new(id, null, null, command.EntityId, DeliveryKind.Test, id,
            $"test/{id:N}", "UptimeHarbor: тестовое уведомление.", DeliveryState.Planned, null, null, image.Index, image.Term)
        {
            ActorUserId = command.ActorUserId
        };
        image = image with { Monitoring = image.Monitoring with { Intents = image.Monitoring.Intents.Add(id, intent) } };
        return Decide(image, command, null);
    }

    private static RevisionImage Decide(RevisionImage image, RevisionCommand command, string? reason)
    {
        OperationOutcome outcome = OperationOutcome.Committed;
        if (reason is not null)
        {
            outcome = OperationOutcome.Rejected;
        }

        CommandResult result = new(command.OperationId, RevisionCodec.Digest(command), outcome,
            image.Index, image.Term, image.Index, reason, null, null, command.ActorRevision);
        return image with { Results = image.Results.Add(command.OperationId, result) };
    }
}
