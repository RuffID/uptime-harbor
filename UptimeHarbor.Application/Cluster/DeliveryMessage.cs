using System.Text;
using System.Text.Json;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

public static class DeliveryMessage
{
    public static string Format(RevisionImage image, MonitoringIntent intent)
    {
        if (intent.Kind is DeliveryKind.Test or DeliveryKind.Summary)
        {
            return intent.Payload;
        }

        MonitoringIncident incident = image.Monitoring.Incidents[intent.IncidentId!.Value];
        using JsonDocument document = JsonDocument.Parse(intent.Payload);
        JsonElement data = document.RootElement;
        string state = data.GetProperty("State").GetString()!;
        MonitoringEvent? previous = null;
        if (intent.Kind == DeliveryKind.Recovery)
        {
            MonitoringEvent recovered = image.Monitoring.Events[intent.EventId!.Value];
            previous = image.Monitoring.Events.Values.Where(item => item.IncidentId == incident.Id
                && item.Sequence < recovered.Sequence).MaxBy(item => item.Sequence)
                ?? throw new InvalidDataException("RecoveryHistoryMissing");
        }

        string label = state switch
        {
            "Partial" => "Доступен не со всех узлов",
            "Unavailable" => "Недоступен",
            "Degraded" => "Частичная потеря наблюдения",
            "Lost" => "Полная потеря наблюдения",
            "Recovered" when incident.Kind == IncidentKind.Coverage => "Наблюдение восстановлено",
            "Recovered" when previous?.Kind == "Partial" => "Частичная недоступность устранена, доступны все направления",
            "Recovered" => "Доступность после полной аварии восстановлена",
            _ => throw new InvalidDataException("UnknownDeliveryState")
        };
        StringBuilder text = new($"UptimeHarbor: {label}\n{incident.Kind} {incident.TargetId:N}\n{data.GetProperty("AtUtc").GetDateTimeOffset():O}\n");
        if (previous is not null)
        {
            using JsonDocument prior = JsonDocument.Parse(previous.Payload);
            foreach (JsonElement node in prior.RootElement.GetProperty("ProblemNodes").EnumerateArray())
            {
                text.AppendLine($"Восстановлено направление: {node.GetGuid():N}");
            }
        }

        foreach (JsonElement node in data.GetProperty("ProblemNodes").EnumerateArray())
        {
            text.AppendLine($"Проблемное направление: {node.GetGuid():N}");
        }

        foreach (JsonElement metadata in data.GetProperty("Metadata").EnumerateArray())
        {
            text.AppendLine($"Узел {metadata.GetProperty("ObserverNodeId").GetGuid():N}: {metadata.GetProperty("EndpointName").GetString()}");
            if (metadata.GetProperty("StartedAtUtc").ValueKind != JsonValueKind.Null)
            {
                text.AppendLine($"Запущен: {metadata.GetProperty("StartedAtUtc").GetDateTimeOffset():O}");
            }
        }

        // Metadata не объединяются; размер сообщения ограничен Telegram, исходный payload сохранён в журнале.
        if (text.Length > 3500)
        {
            return text.ToString(0, 3490) + "\n…";
        }

        return text.ToString();
    }
}
