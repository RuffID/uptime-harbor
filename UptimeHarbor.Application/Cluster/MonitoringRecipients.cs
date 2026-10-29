using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

public static class MonitoringRecipients
{
    public static RevisionImage Plan(RevisionImage image, MonitoringIncident incident, MonitoringEvent acceptedEvent,
        bool opening, bool recovered)
    {
        DeliveryKind kind = DeliveryKind.Update;
        if (opening)
        {
            kind = DeliveryKind.Problem;
        }
        else if (recovered)
        {
            kind = DeliveryKind.Recovery;
        }

        if (recovered)
        {
            foreach (MonitoringIntent problem in image.Monitoring.Intents.Values.Where(intent => intent.IncidentId == incident.Id
                && intent.Kind is DeliveryKind.Problem or DeliveryKind.Update).OrderBy(intent => intent.Index).ThenBy(intent => intent.Id).ToArray())
            {
                if (HasConfirmedProblem(image, incident.Id, problem.ChatId) && ChatExists(image, problem.ChatId))
                {
                    image = Add(image, incident, acceptedEvent, problem.ChatId, problem.BindingGeneration, kind);
                }
            }

            return image;
        }

        DeletedEntityKind routeKind = DeletedEntityKind.Binding;
        if (incident.Kind == IncidentKind.Node || image.Monitoring.Nodes.ContainsKey(incident.TargetId))
        {
            routeKind = DeletedEntityKind.NodeChatRoute;
        }

        foreach (AcceptedValue route in image.Values.Values.Where(cell => !cell.Deleted && cell.Kind == routeKind
            && cell.Value.ParentId == incident.TargetId).OrderBy(cell => cell.Id))
        {
            Guid chatId = route.Value.ChatId!.Value;
            if (ChatExists(image, chatId))
            {
                image = Add(image, incident, acceptedEvent, chatId, route.Value.Generation, kind);
            }
        }

        return image;
    }

    public static bool ChatExists(RevisionImage image, Guid chatId)
    {
        AcceptedValue? chat = image.Values.GetValueOrDefault(chatId);
        return chat is not null && !chat.Deleted && chat.Kind == DeletedEntityKind.Chat;
    }

    public static bool HasConfirmedProblem(RevisionImage image, Guid incidentId, Guid chatId)
    {
        // Сам intent, локальный marker или факт его включения в сводку не являются подтверждением.
        foreach (MonitoringIntent intent in image.Monitoring.Intents.Values.Where(intent => intent.IncidentId == incidentId
            && intent.ChatId == chatId && intent.Kind is DeliveryKind.Problem or DeliveryKind.Update))
        {
            if (!image.Monitoring.ConfirmedDeliveries.TryGetValue(intent.Id, out MonitoringDeliveryEvidence? evidence))
            {
                continue;
            }

            if (intent.State != DeliveryState.Succeeded || intent.CompletedAtUtc != evidence.SucceededAtUtc
                || evidence.IntentId != intent.Id || evidence.AcceptedIndex > image.Index || evidence.AcceptedTerm > image.Term
                || evidence.AcceptedIndex < image.Monitoring.Events[intent.EventId!.Value].Index)
            {
                throw new InvalidDataException("DeliveryEvidenceMismatch");
            }

            DeliveryAttempt attempt = AcceptedAttempt(image, evidence);
            if (attempt.Outcome == AttemptOutcome.Succeeded)
            {
                return true;
            }
        }

        return false;
    }

    public static DeliveryAttempt AcceptedAttempt(RevisionImage image, MonitoringDeliveryEvidence evidence)
    {
        AcceptedDeliveryAttempt attempt = image.Monitoring.Attempts.GetValueOrDefault(evidence.AttemptId)
            ?? throw new InvalidDataException("DeliveryAttemptHistoryMissing");
        if (attempt.IntentId != evidence.IntentId || attempt.Number != evidence.AttemptNumber
            || attempt.Outcome != AttemptOutcome.Succeeded || attempt.Index != evidence.AcceptedIndex
            || attempt.Term != evidence.AcceptedTerm || attempt.Authority.LeaderNodeId != evidence.SenderNodeId
            || attempt.Authority.SenderEpoch != evidence.SenderEpoch || attempt.StartedAtUtc != evidence.StartedAtUtc
            || attempt.CompletedAtUtc != evidence.SucceededAtUtc || attempt.TelegramMessageId != evidence.TelegramMessageId)
        {
            throw new InvalidDataException("DeliveryEvidenceMismatch");
        }

        return DeliveryProjection.Attempt(image, attempt);
    }

    public static RevisionImage Add(RevisionImage image, MonitoringIncident incident, MonitoringEvent acceptedEvent,
        Guid chatId, Guid bindingGeneration, DeliveryKind kind)
    {
        string logical = $"{incident.Id:N}/{kind}/{chatId:N}";
        if (kind == DeliveryKind.Update)
        {
            logical = $"{incident.Id:N}/{acceptedEvent.Id:N}/{chatId:N}";
        }

        Guid id = MonitoringKeys.Id(logical);
        if (image.Monitoring.Intents.TryGetValue(id, out MonitoringIntent? prior))
        {
            if (prior.State == DeliveryState.Cancelled && !HasConfirmedProblem(image, incident.Id, chatId))
            {
                MonitoringIntent resumed = prior with
                {
                    State = DeliveryState.Planned, CompletedAtUtc = null, BindingGeneration = bindingGeneration,
                    EventId = acceptedEvent.Id, Payload = acceptedEvent.Payload,
                    Index = image.Index, Term = image.Term
                };
                return image with { Monitoring = image.Monitoring with { Intents = image.Monitoring.Intents.SetItem(id, resumed) } };
            }

            return image;
        }

        MonitoringIntent intent = new(id, incident.Id, acceptedEvent.Id, chatId, kind, bindingGeneration,
            logical, acceptedEvent.Payload, DeliveryState.Planned, null, null, image.Index, image.Term);
        return image with { Monitoring = image.Monitoring with { Intents = image.Monitoring.Intents.Add(id, intent) } };
    }

    public static RevisionImage Cancel(RevisionImage image, Func<MonitoringIntent, bool> predicate, DateTimeOffset atUtc)
    {
        image = MonitoringBacklog.RevokeUnstarted(image, predicate, atUtc);
        foreach (MonitoringIntent intent in image.Monitoring.Intents.Values.Where(predicate).ToArray())
        {
            if (intent.State is DeliveryState.Planned or DeliveryState.RetryDue)
            {
                MonitoringIntent cancelled = intent with
                {
                    State = DeliveryState.Cancelled, CompletedAtUtc = atUtc, NextAttemptAtUtc = null, Index = image.Index, Term = image.Term
                };
                image = image with { Monitoring = image.Monitoring with { Intents = image.Monitoring.Intents.SetItem(intent.Id, cancelled) } };
            }
        }

        return image;
    }

    public static RevisionImage ConfigurationChanged(RevisionImage image, AcceptedValue? before, AcceptedValue after,
        DateTimeOffset atUtc)
    {
        if (after.Kind is DeletedEntityKind.Server or DeletedEntityKind.Container)
        {
            if (after.Deleted || before is not null && before.Value.Generation != after.Value.Generation)
            {
                image = MonitoringIncidents.Supersede(image, after.Id, atUtc, after.Deleted);
            }

            if (after.Deleted)
            {
                image = Cancel(image, intent => intent.IncidentId.HasValue
                    && image.Monitoring.Incidents[intent.IncidentId.Value].TargetId == after.Id, atUtc);
            }
        }
        else if (after.Kind == DeletedEntityKind.Chat && after.Deleted)
        {
            image = Cancel(image, intent => intent.ChatId == after.Id, atUtc);
        }
        else if (after.Kind is DeletedEntityKind.Binding or DeletedEntityKind.NodeChatRoute)
        {
            if (after.Deleted)
            {
                image = Cancel(image, intent => intent.ChatId == after.Value.ChatId && intent.Kind != DeliveryKind.Recovery
                    && intent.IncidentId.HasValue && image.Monitoring.Incidents[intent.IncidentId.Value].TargetId == after.Value.ParentId, atUtc);
            }
            else
            {
                foreach (MonitoringIncident incident in image.Monitoring.Incidents.Values.Where(item => item.TargetId == after.Value.ParentId
                    && item.CompletedAtUtc is null).ToArray())
                {
                    if (!HasConfirmedProblem(image, incident.Id, after.Value.ChatId!.Value))
                    {
                        MonitoringEvent latest = image.Monitoring.Events.Values.Where(item => item.IncidentId == incident.Id)
                            .MaxBy(item => item.Sequence)!;
                        image = Add(image, incident, latest, after.Value.ChatId.Value, after.Value.Generation, DeliveryKind.Problem);
                    }
                }
            }
        }

        return image;
    }
}
