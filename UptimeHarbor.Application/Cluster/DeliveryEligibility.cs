using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

/// <summary>Повторная проверка принятой конфигурации непосредственно перед внешним вызовом.</summary>
public static class DeliveryEligibility
{
    public static bool Allows(RevisionImage image, MonitoringIntent intent)
    {
        if (!MonitoringRecipients.ChatExists(image, intent.ChatId))
        {
            return false;
        }

        if (intent.Kind == DeliveryKind.Test)
        {
            return intent.ActorUserId.HasValue
                && AcceptedAdministratorAccessPolicy.IsActiveAdministrator(RevisionAccounts.Find(image, intent.ActorUserId.Value));
        }

        if (intent.Kind == DeliveryKind.Summary)
        {
            MonitoringSummary summary = image.Monitoring.Summaries.Values.Single(item => item.IntentId == intent.Id);
            return summary.SourceIntentIds.All(id => Allows(image, image.Monitoring.Intents[id]));
        }

        MonitoringIncident incident = image.Monitoring.Incidents[intent.IncidentId!.Value];
        if (incident.Completion is IncidentCompletion.Deleted or IncidentCompletion.SupersededConfiguration)
        {
            return false;
        }

        if (image.Monitoring.Nodes.ContainsKey(incident.TargetId))
        {
            return HasRoute(image, intent, incident, DeletedEntityKind.NodeChatRoute);
        }

        AcceptedValue? target = image.Values.GetValueOrDefault(incident.TargetId);
        if (target is null || target.Deleted || !target.Value.Enabled || target.Value.Generation != incident.CheckGeneration)
        {
            return false;
        }

        return HasRoute(image, intent, incident, DeletedEntityKind.Binding);
    }

    private static bool HasRoute(RevisionImage image, MonitoringIntent intent, MonitoringIncident incident, DeletedEntityKind kind)
    {
        if (intent.Kind == DeliveryKind.Recovery)
        {
            return incident.Completion == IncidentCompletion.Recovered
                && MonitoringRecipients.HasConfirmedProblem(image, incident.Id, intent.ChatId);
        }

        return image.Values.Values.Any(route => !route.Deleted && route.Kind == kind
            && route.Value.ParentId == incident.TargetId && route.Value.ChatId == intent.ChatId
            && route.Value.Generation == intent.BindingGeneration);
    }
}
