using System.Collections.Immutable;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

/// <summary>Неизменяемый состав сводок; не выполняет отправку и не подтверждает доставку.</summary>
public static class MonitoringBacklog
{
    public static RevisionImage RevokeUnstarted(RevisionImage image, Func<MonitoringIntent, bool> revoked, DateTimeOffset atUtc)
    {
        foreach (MonitoringSummary summary in image.Monitoring.Summaries.Values.OrderBy(item => item.Id))
        {
            MonitoringIntent delivery = image.Monitoring.Intents[summary.IntentId];
            if (delivery.State is not (DeliveryState.Planned or DeliveryState.RetryDue)
                || !summary.SourceIntentIds.Any(id => revoked(image.Monitoring.Intents[id])))
            {
                continue;
            }

            // Принятый состав неизменяем: отзывается вся часть, остальные источники получают новый план.
            MonitoringImage next = image.Monitoring with { Intents = image.Monitoring.Intents.SetItem(delivery.Id, delivery with
            {
                State = DeliveryState.Cancelled, CompletedAtUtc = atUtc, Index = image.Index, Term = image.Term
            }) };
            foreach (Guid id in summary.SourceIntentIds)
            {
                MonitoringIntent source = next.Intents[id];
                if (source.State != DeliveryState.SuppressedBySummary || source.SummaryId != summary.Id)
                {
                    continue;
                }

                DeliveryState state = DeliveryState.Planned;
                DateTimeOffset? completed = null;
                if (revoked(source))
                {
                    state = DeliveryState.Cancelled;
                    completed = atUtc;
                }

                next = next with { Intents = next.Intents.SetItem(id, source with
                {
                    State = state, SummaryId = null, CompletedAtUtc = completed, Index = image.Index, Term = image.Term
                }) };
            }

            image = image with { Monitoring = next };
        }

        return image;
    }

    public static RevisionImage Plan(RevisionImage image, RevisionCommand command)
    {
        long cut = command.Monitoring!.CutIndex;
        var recipients = image.Monitoring.Intents.Values.Where(intent => intent.Index <= cut
            && intent.IncidentId.HasValue && intent.State is DeliveryState.Planned or DeliveryState.RetryDue)
            .GroupBy(intent => intent.ChatId).OrderBy(group => group.Key);
        foreach (var chat in recipients)
        {
            if (!MonitoringRecipients.ChatExists(image, chat.Key))
            {
                continue;
            }

            List<MonitoringIntent> part = [];
            string text = "";
            int number = 1;
            foreach (var incidentIntents in chat.GroupBy(intent => intent.IncidentId!.Value)
                .OrderBy(group => image.Monitoring.Incidents[group.Key].OpenedAtUtc).ThenBy(group => group.Key))
            {
                MonitoringIncident incident = image.Monitoring.Incidents[incidentIntents.Key];
                if (!incident.CompletedAtUtc.HasValue || incident.Completion != IncidentCompletion.Recovered
                    || incident.Index > cut || MonitoringRecipients.HasConfirmedProblem(image, incident.Id, chat.Key))
                {
                    // Открытые аварии и обычный recovery предупреждённого чата остаются отдельными intents.
                    continue;
                }

                string line = $"{incident.Kind} {incident.TargetId:N}: {incident.OpenedAtUtc:O} — {incident.CompletedAtUtc:O}\n";
                if (text.Length + line.Length > 3500)
                {
                    image = AddPart(image, command.OperationId, chat.Key, number, cut, text, part);
                    number++;
                    text = "";
                    part = [];
                }

                text += line;
                part.AddRange(incidentIntents.OrderBy(intent => intent.Index).ThenBy(intent => intent.Id));
            }

            if (part.Count > 0)
            {
                image = AddPart(image, command.OperationId, chat.Key, number, cut, text, part);
            }
        }

        return image;
    }

    private static RevisionImage AddPart(RevisionImage image, Guid planId, Guid chatId, int number,
        long cut, string text, IReadOnlyList<MonitoringIntent> sources)
    {
        Guid id = MonitoringKeys.Id($"summary/{planId:N}/{chatId:N}/{number}");
        Guid intentId = MonitoringKeys.Id($"summary-intent/{id:N}");
        MonitoringSummary summary = new(id, planId, chatId, number, cut, intentId, text,
            sources.Select(item => item.Id).ToImmutableArray(), image.Index, image.Term);
        MonitoringIntent summaryIntent = new(intentId, null, null, chatId, DeliveryKind.Summary, planId,
            $"summary/{id:N}", text, DeliveryState.Planned, null, null, image.Index, image.Term);
        MonitoringImage next = image.Monitoring with
        {
            Summaries = image.Monitoring.Summaries.Add(id, summary),
            Intents = image.Monitoring.Intents.Add(intentId, summaryIntent)
        };
        foreach (MonitoringIntent source in sources)
        {
            next = next with { Intents = next.Intents.SetItem(source.Id, source with
            {
                State = DeliveryState.SuppressedBySummary, SummaryId = id, Index = image.Index, Term = image.Term
            }) };
        }

        return image with { Monitoring = next };
    }
}
