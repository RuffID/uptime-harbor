using System.Collections.Immutable;
using System.Text.Json;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

public static class MonitoringIncidents
{
    public static RevisionImage Advance(RevisionImage image, MonitoringTarget target, MonitoringEvaluation evaluation)
    {
        IncidentKind kind = IncidentKind.Target;
        if (target.IsNode)
        {
            kind = IncidentKind.Node;
        }

        MonitoringIncident? open = Open(image, target, kind);
        if (evaluation.Availability is AvailabilityState.Partial or AvailabilityState.Unavailable)
        {
            ImmutableArray<Guid> problems = evaluation.ProblemNodes;
            if (open is not null && evaluation.Coverage != CoverageState.Complete)
            {
                problems = problems.Union(open.ProblemNodes.Where(evaluation.MissingNodes.Contains)).Order().ToImmutableArray();
            }

            string stateKey = evaluation.Availability + "/" + string.Join(",", problems.Order());
            if (target.IsNode)
            {
                var failures = evaluation.Failures.ToBuilder();
                if (open is not null)
                {
                    foreach (Guid missing in evaluation.MissingNodes)
                    {
                        if (open.Failures.TryGetValue(missing, out string? reason))
                        {
                            failures[missing] = reason;
                        }
                    }
                }

                evaluation = evaluation with { Failures = failures.ToImmutable() };
                stateKey += "/" + MonitoringKeys.Digest(evaluation.Failures.OrderBy(item => item.Key).ToArray());
            }

            image = Transition(image, target, evaluation, kind, open, stateKey, problems, false);
        }
        else if (open is not null && evaluation.Availability == AvailabilityState.Available
            && evaluation.Coverage == CoverageState.Complete)
        {
            image = Transition(image, target, evaluation, kind, open, "Recovered", [], true);
        }

        MonitoringIncident? coverage = Open(image, target, IncidentKind.Coverage);
        if (evaluation.Coverage is CoverageState.Degraded or CoverageState.Lost)
        {
            string stateKey = evaluation.Coverage + "/" + string.Join(",", evaluation.MissingNodes.Order());
            image = Transition(image, target, evaluation, IncidentKind.Coverage, coverage,
                stateKey, evaluation.MissingNodes, false);
        }
        else if (coverage is not null && evaluation.Coverage == CoverageState.Complete)
        {
            image = Transition(image, target, evaluation, IncidentKind.Coverage, coverage, "Recovered", [], true);
        }

        return image;
    }

    public static MonitoringIncident? Open(RevisionImage image, MonitoringTarget target, IncidentKind kind)
    {
        return image.Monitoring.Incidents.Values.SingleOrDefault(incident => incident.TargetId == target.Id
            && incident.CheckGeneration == target.CheckGeneration && incident.Kind == kind && incident.CompletedAtUtc is null);
    }

    private static RevisionImage Transition(RevisionImage image, MonitoringTarget target, MonitoringEvaluation evaluation,
        IncidentKind kind, MonitoringIncident? incident, string stateKey, ImmutableArray<Guid> problems, bool recovered)
    {
        DateTimeOffset? last = image.Monitoring.Events.Values
            .Where(item => image.Monitoring.Incidents[item.IncidentId].TargetId == target.Id
                && image.Monitoring.Incidents[item.IncidentId].Kind == kind)
            .Select(item => (DateTimeOffset?)item.AtUtc).Max();
        if (evaluation.AtUtc < last || incident?.StateKey == stateKey)
        {
            // Late evidence остаётся в observations; уже зафиксированная история не переписывается.
            return image;
        }

        bool opening = incident is null;
        if (incident is null)
        {
            string transitionKey = MonitoringKeys.Digest(new { target.Id, target.CheckGeneration, kind, evaluation.AtUtc, evaluation.FrontierDigest });
            Guid id = MonitoringKeys.Id($"incident/{target.Id:N}/{target.CheckGeneration:N}/{kind}/{transitionKey}");
            incident = new(id, target.Id, target.CheckGeneration, kind, transitionKey, evaluation.AtUtc,
                null, null, stateKey, 0, [], image.Index, image.Term);
        }

        long sequence = incident.EventSequence + 1;
        string eventKind = stateKey.Split('/')[0];
        Guid eventId = MonitoringKeys.Id($"event/{incident.Id:N}/{sequence}/{eventKind}");
        string payload = JsonSerializer.Serialize(new
        {
            TargetId = target.Id, target.IsNode, Kind = kind.ToString(), State = eventKind,
            AtUtc = evaluation.AtUtc, ProblemNodes = problems,
            Failures = evaluation.Failures.OrderBy(item => item.Key).Select(item => new { ObserverNodeId = item.Key, Kind = item.Value }),
            Metadata = evaluation.Metadata.OrderBy(item => item.Key).Select(item => new
            {
                ObserverNodeId = item.Key, item.Value.EndpointName, item.Value.StartedAtUtc
            })
        });
        incident = incident with
        {
            StateKey = stateKey, EventSequence = sequence, ProblemNodes = problems,
            Failures = evaluation.Failures,
            Index = image.Index, Term = image.Term
        };
        if (recovered)
        {
            incident = incident with { CompletedAtUtc = evaluation.AtUtc, Completion = IncidentCompletion.Recovered };
        }

        MonitoringEvent acceptedEvent = new(eventId, incident.Id, sequence, eventKind,
            evaluation.AtUtc, evaluation.FrontierDigest, payload, image.Index, image.Term);
        image = image with
        {
            Monitoring = image.Monitoring with
            {
                Incidents = image.Monitoring.Incidents.SetItem(incident.Id, incident),
                Events = image.Monitoring.Events.Add(eventId, acceptedEvent)
            }
        };
        return MonitoringRecipients.Plan(image, incident, acceptedEvent, opening, recovered);
    }

    public static RevisionImage Supersede(RevisionImage image, Guid targetId, DateTimeOffset atUtc, bool deleted)
    {
        foreach (MonitoringIncident incident in image.Monitoring.Incidents.Values.Where(item => item.TargetId == targetId
            && item.CompletedAtUtc is null).ToArray())
        {
            DateTimeOffset completedAt = atUtc;
            if (completedAt < incident.OpenedAtUtc)
            {
                completedAt = incident.OpenedAtUtc;
            }

            IncidentCompletion completion = IncidentCompletion.SupersededConfiguration;
            if (deleted)
            {
                completion = IncidentCompletion.Deleted;
            }

            MonitoringIncident closed = incident with
            {
                CompletedAtUtc = completedAt, Completion = completion, Index = image.Index, Term = image.Term
            };
            image = image with { Monitoring = image.Monitoring with { Incidents = image.Monitoring.Incidents.SetItem(closed.Id, closed) } };
            image = MonitoringRecipients.Cancel(image, intent => intent.IncidentId == incident.Id, completedAt);
        }

        return image;
    }
}
