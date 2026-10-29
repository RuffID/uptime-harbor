using System.Text.Json;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

/// <summary>Преобразование accepted monitoring image в существующие проекции одной apply-транзакции.</summary>
public static class MonitoringProjection
{
    public static IReadOnlyList<ProjectionRecord> Changes(RevisionImage before, RevisionImage after, bool deliveryIdentityOnly = false)
    {
        List<ProjectionRecord> records = [];
        ClusterIdentity cluster = new(after.ClusterId, after.Generation);
        foreach (MonitoringObservation fact in after.Monitoring.Observations.Values.OrderBy(item => item.Id))
        {
            if (before.Monitoring.Observations.ContainsKey(fact.Id))
            {
                continue;
            }

            ObservationAcceptance accepted = after.Monitoring.Acceptances[fact.Id];
            records.Add(new ObserverResult(fact.Id, new JournalPosition(cluster, accepted.Index, accepted.Term),
                fact.TargetId, fact.ObserverNodeId, fact.StorageGeneration, fact.CheckGeneration, fact.ConfigRevision,
                fact.Sequence, fact.CheckedAtUtc, fact.DurationMilliseconds, fact.Succeeded, fact.FailureKind,
                fact.EndpointName, fact.StartedAtUtc, accepted.ConsecutiveFailures, accepted.HistoricalOnly, MonitoringKeys.Digest(fact)));
        }

        foreach (MonitoringStream stream in after.Monitoring.Streams.Values.OrderBy(item => item.Id))
        {
            bool open = AssemblyOpen(after, stream);
            if (before.Monitoring.Streams.GetValueOrDefault(stream.Id) == stream && AssemblyOpen(before, stream) == open)
            {
                continue;
            }

            JournalPosition position = new(cluster, stream.Index, stream.Term);
            records.Add(new ObservationFrontier(stream.Id, position, stream.TargetId, stream.ObserverNodeId,
                stream.StorageGeneration, stream.CheckGeneration, stream.Sequence, stream.WatermarkUtc, stream.BatchId, open));
        }

        foreach (MonitoringEvaluation evaluation in after.Monitoring.Live.Values.OrderBy(item => item.TargetId))
        {
            if (before.Monitoring.Live.GetValueOrDefault(evaluation.TargetId) != evaluation)
            {
                records.Add(new TargetObservationState(evaluation.TargetId, new JournalPosition(cluster, evaluation.Index, evaluation.Term),
                    evaluation.CheckGeneration, evaluation.Availability, evaluation.Coverage, evaluation.AtUtc,
                    evaluation.FrontierDigest, JsonSerializer.Serialize(evaluation.ProblemNodes)));
            }
        }

        foreach (MonitoringIncident incident in after.Monitoring.Incidents.Values.OrderBy(item => item.Id))
        {
            if (before.Monitoring.Incidents.GetValueOrDefault(incident.Id) != incident)
            {
                records.Add(Incident(cluster, incident));
            }
        }

        foreach (MonitoringEvent acceptedEvent in after.Monitoring.Events.Values.OrderBy(item => item.Id))
        {
            if (!before.Monitoring.Events.ContainsKey(acceptedEvent.Id))
            {
                records.Add(new IncidentEvent(acceptedEvent.Id, new JournalPosition(cluster, acceptedEvent.Index, acceptedEvent.Term),
                    acceptedEvent.IncidentId, acceptedEvent.Sequence, acceptedEvent.Kind, acceptedEvent.AtUtc,
                    acceptedEvent.FrontierDigest, acceptedEvent.Payload));
            }
        }

        foreach (MonitoringIntent intent in after.Monitoring.Intents.Values.OrderBy(item => item.Id))
        {
            if (before.Monitoring.Intents.GetValueOrDefault(intent.Id) != intent)
            {
                if (deliveryIdentityOnly && intent.State == DeliveryState.Succeeded)
                {
                    records.Add(Intent(cluster, intent with { State = DeliveryState.Planned }));
                }
                else
                {
                    records.Add(Intent(cluster, intent));
                }
                if (intent.Kind == DeliveryKind.Recovery)
                {
                    MonitoringIntent problem = after.Monitoring.Intents.Values.Where(item => item.IncidentId == intent.IncidentId
                        && item.ChatId == intent.ChatId && after.Monitoring.ConfirmedDeliveries.ContainsKey(item.Id))
                        .OrderBy(item => item.Index).ThenBy(item => item.Id).First();
                    Guid entitlementId = MonitoringKeys.Id($"entitlement/{intent.IncidentId:N}/{intent.ChatId:N}");
                    RecoveryEntitlement entitlement = new(entitlementId, new JournalPosition(cluster, intent.Index, intent.Term),
                        intent.IncidentId!.Value, intent.ChatId, problem.Id);
                    entitlement.AssignRecovery(intent.Id);
                    records.Add(entitlement);
                }
            }
        }

        foreach (MonitoringSummary summary in after.Monitoring.Summaries.Values.OrderBy(item => item.Id))
        {
            if (before.Monitoring.Summaries.ContainsKey(summary.Id))
            {
                continue;
            }

            JournalPosition position = new(cluster, summary.Index, summary.Term);
            records.Add(new DeliverySummary(summary.Id, position, summary.PlanId, summary.ChatId,
                summary.PartNumber, summary.CutIndex, summary.IntentId, summary.Text));
            foreach (Guid sourceId in summary.SourceIntentIds)
            {
                MonitoringIntent source = after.Monitoring.Intents[sourceId];
                Guid id = MonitoringKeys.Id($"summary-source/{summary.Id:N}/{sourceId:N}");
                records.Add(new SummarySource(id, position, summary.Id, source.EventId!.Value, sourceId));
            }
        }

        AddAssignments(before, after, records);
        return records;
    }

    public static SharedIncident Incident(ClusterIdentity cluster, MonitoringIncident incident)
    {
        SharedIncident record = new(incident.Id, new JournalPosition(cluster, incident.Index, incident.Term), incident.TargetId,
            incident.CheckGeneration, incident.Kind, incident.TransitionKey, incident.OpenedAtUtc);
        if (incident.Completion.HasValue)
        {
            record.Complete(incident.Completion.Value, incident.CompletedAtUtc!.Value);
        }

        return record;
    }

    public static DeliveryIntent Intent(ClusterIdentity cluster, MonitoringIntent intent)
    {
        DeliveryIntent record = new(intent.Id, new JournalPosition(cluster, intent.Index, intent.Term), intent.IncidentId,
            intent.EventId, intent.ChatId, intent.Kind, intent.BindingGeneration, intent.LogicalKey, intent.Payload);
        if (intent.State == DeliveryState.Cancelled)
        {
            record.CancelNotStarted(intent.CompletedAtUtc!.Value);
        }
        else if (intent.State == DeliveryState.SuppressedBySummary)
        {
            record.SuppressBySummary(intent.SummaryId!.Value);
        }
        else if (intent.State != DeliveryState.Planned)
        {
            throw new InvalidDataException("DeliveryAttemptProjectionRequired");
        }

        return record;
    }

    private static bool AssemblyOpen(RevisionImage image, MonitoringStream stream)
    {
        MonitoringEvaluation? history = image.Monitoring.History.GetValueOrDefault(stream.TargetId);
        return history is null || history.CheckGeneration != stream.CheckGeneration || history.AtUtc < stream.WatermarkUtc;
    }

    public static IReadOnlyList<ObserverAssignment> Assignments(RevisionImage image)
    {
        List<ObserverAssignment> assignments = [];
        foreach (AcceptedValue cell in image.Values.Values.Where(cell => !cell.Deleted
            && cell.Kind is DeletedEntityKind.Server or DeletedEntityKind.Container).OrderBy(cell => cell.Id))
        {
            foreach (Guid observer in cell.Value.ObserverNodes.Order())
            {
                Guid id = MonitoringKeys.Id($"assignment/{cell.Id:N}/{cell.Value.Generation:N}/{observer:N}");
                assignments.Add(new ObserverAssignment(id, new JournalPosition(new ClusterIdentity(image.ClusterId, image.Generation),
                    cell.Revision, image.Term), cell.Id, observer, cell.Value.Generation, cell.Revision, cell.Value.AssignedAtUtc));
            }
        }

        return assignments;
    }

    private static void AddAssignments(RevisionImage before, RevisionImage after, List<ProjectionRecord> records)
    {
        Dictionary<Guid, ObserverAssignment> old = Assignments(before).ToDictionary(item => item.Id);
        Dictionary<Guid, ObserverAssignment> current = Assignments(after).ToDictionary(item => item.Id);
        foreach (ObserverAssignment assignment in current.Values)
        {
            if (!old.TryGetValue(assignment.Id, out ObserverAssignment? prior) || prior.ConfigRevision != assignment.ConfigRevision)
            {
                records.Add(assignment);
            }
        }

        foreach (ObserverAssignment removed in old.Values.Where(item => !current.ContainsKey(item.Id)))
        {
            AcceptedValue target = after.Values[removed.TargetId];
            DateTimeOffset atUtc = target.DeletedAtUtc ?? target.Value.AssignedAtUtc;
            records.Add(new Tombstone(removed.Id, new JournalPosition(new ClusterIdentity(after.ClusterId, after.Generation),
                after.Index, after.Term), DeletedEntityKind.ObserverAssignment, atUtc));
        }
    }
}
