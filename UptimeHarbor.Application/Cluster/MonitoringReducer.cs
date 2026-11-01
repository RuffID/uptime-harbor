using System.Collections.Immutable;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

/// <summary>Наблюдения, сведение и состав backlog принимаются только общим ordered pipeline.</summary>
public static class MonitoringReducer
{
    public static RevisionImage Apply(RevisionImage image, RevisionCommand command)
    {
        MonitoringCommand payload = command.Monitoring!;
        if (!image.Monitoring.Nodes.TryGetValue(command.OriginNodeId, out Guid storage)
            || storage != payload.StorageGeneration)
        {
            return Decide(image, command, OperationOutcome.Rejected, "ObserverNotAdmitted");
        }

        if (command.Action != "RecordObservations" && payload.CoordinatorTerm != image.Term)
        {
            return Decide(image, command, OperationOutcome.Rejected, "CoordinatorTermChanged");
        }

        if (command.Action == "PlanBacklogDelivery")
        {
            if (payload.CutIndex >= image.Index)
            {
                return Decide(image, command, OperationOutcome.Rejected, "BacklogCutAhead");
            }

            image = MonitoringBacklog.Plan(image, command);
            return Decide(image, command, OperationOutcome.Committed, null);
        }

        MonitoringTarget? target = MonitoringTargets.Find(image, command.EntityId);
        if (command.Action == "RecordObservations")
        {
            return Record(image, command, target);
        }

        if (target is null || target.Observers.IsEmpty || command.BaseRevision != target.ConfigRevision)
        {
            return Decide(image, command, OperationOutcome.Rejected, "TargetConfigurationChanged");
        }

        if (payload.FrontierDigest != MonitoringTargets.Frontier(image, target))
        {
            return Decide(image, command, OperationOutcome.Rejected, "ObservationFrontierChanged");
        }

        DateTimeOffset atUtc = payload.EvaluationTimeUtc!.Value;
        if (atUtc > command.CreatedAtUtc.AddSeconds(5))
        {
            return Decide(image, command, OperationOutcome.Rejected, "EvaluationClockUnknown");
        }

        MonitoringEvaluation? previous;
        ImmutableDictionary<Guid, MonitoringEvaluation> evaluations = image.Monitoring.Live;
        if (payload.Historical)
        {
            evaluations = image.Monitoring.History;
            DateTimeOffset? watermark = HistoricalWatermark(image, target);
            if (!watermark.HasValue || atUtc > watermark)
            {
                return Decide(image, command, OperationOutcome.Rejected, "HistoricalRangeIncomplete");
            }
        }

        previous = evaluations.GetValueOrDefault(target.Id);
        if (previous is not null && previous.CheckGeneration == target.CheckGeneration && previous.AtUtc >= atUtc)
        {
            return Decide(image, command, OperationOutcome.Committed, null);
        }

        MonitoringEvaluation evaluation = MonitoringAvailability.Evaluate(image, target, atUtc);
        MonitoringImage monitoring = image.Monitoring;
        if (payload.Historical)
        {
            monitoring = monitoring with { History = evaluations.SetItem(target.Id, evaluation) };
            foreach (MonitoringStream stream in monitoring.Streams.Values.Where(item => item.TargetId == target.Id
                && item.CheckGeneration == target.CheckGeneration).ToArray())
            {
                monitoring = monitoring with { Streams = monitoring.Streams.SetItem(stream.Id,
                    stream with { Index = image.Index, Term = image.Term }) };
            }
        }
        else
        {
            monitoring = monitoring with { Live = evaluations.SetItem(target.Id, evaluation) };
        }

        image = image with { Monitoring = monitoring };
        image = MonitoringIncidents.Advance(image, target, evaluation);
        return Decide(image, command, OperationOutcome.Committed, null);
    }

    public static DateTimeOffset? HistoricalWatermark(RevisionImage image, MonitoringTarget target)
    {
        DateTimeOffset? through = null;
        foreach (Guid observer in target.Observers)
        {
            Guid storage = image.Monitoring.Nodes.GetValueOrDefault(observer);
            Guid streamId = MonitoringKeys.Stream(target.Id, observer, storage, target.CheckGeneration);
            if (!image.Monitoring.Streams.TryGetValue(streamId, out MonitoringStream? stream))
            {
                return null;
            }

            if (!through.HasValue || stream.WatermarkUtc < through)
            {
                through = stream.WatermarkUtc;
            }
        }

        return through;
    }

    private static RevisionImage Record(RevisionImage image, RevisionCommand command, MonitoringTarget? target)
    {
        MonitoringImage next = image.Monitoring;
        foreach (MonitoringObservation observation in command.Monitoring!.Observations)
        {
            if (next.Observations.TryGetValue(observation.Id, out MonitoringObservation? prior))
            {
                if (MonitoringKeys.Digest(prior) != MonitoringKeys.Digest(observation))
                {
                    throw new InvalidDataException("ObservationIdReuse");
                }

                continue;
            }

            Guid streamId = MonitoringKeys.Stream(observation.TargetId, observation.ObserverNodeId,
                observation.StorageGeneration, observation.CheckGeneration);
            MonitoringStream? stream = next.Streams.GetValueOrDefault(streamId);
            long expected = (stream?.Sequence ?? 0) + 1;
            if (observation.Sequence != expected)
            {
                return Decide(image, command, OperationOutcome.Rejected, "ObservationGap");
            }

            // Старое поколение сохраняется как evidence, но никогда не становится текущим результатом.
            bool current = target is not null && target.CheckGeneration == observation.CheckGeneration
                && target.ConfigRevision == observation.ConfigRevision && target.Observers.Contains(observation.ObserverNodeId);
            if (current && observation.Succeeded && !target!.IsNode && observation.EndpointName is null)
            {
                return Decide(image, command, OperationOutcome.Rejected, "EndpointMetadataMissing");
            }

            if (current && observation.Succeeded && !target!.IsNode
                && image.Values[target.Id].Kind == DeletedEntityKind.Container && observation.StartedAtUtc is null)
            {
                return Decide(image, command, OperationOutcome.Rejected, "EndpointStartMissing");
            }

            if (target is not null && observation.CheckGeneration == target.CheckGeneration
                && !target.Observers.Contains(observation.ObserverNodeId))
            {
                return Decide(image, command, OperationOutcome.Rejected, "ObserverNotAssigned");
            }

            if (observation.ConfigRevision > (target?.ConfigRevision ?? 0) && target is not null)
            {
                return Decide(image, command, OperationOutcome.Rejected, "ConfigurationAhead");
            }

            if (!current && !WasAssigned(image, observation))
            {
                return Decide(image, command, OperationOutcome.Rejected, "ObservationConfigurationUnknown");
            }

            bool validClock = observation.ClockConfirmed && (stream is null || observation.CheckedAtUtc >= stream.WatermarkUtc);
            int failures = 0;
            if (!observation.Succeeded && validClock)
            {
                failures = checked((stream?.ConsecutiveFailures ?? 0) + 1);
            }

            DateTimeOffset watermark = stream?.WatermarkUtc ?? DateTimeOffset.UnixEpoch;
            if (validClock && observation.CheckedAtUtc > watermark)
            {
                watermark = observation.CheckedAtUtc;
            }

            MonitoringStream frontier = new(streamId, observation.TargetId, observation.ObserverNodeId,
                observation.StorageGeneration, observation.CheckGeneration, observation.Sequence, watermark,
                command.OperationId, failures, image.Index, image.Term);
            next = next with
            {
                Observations = next.Observations.Add(observation.Id, observation),
                Acceptances = next.Acceptances.Add(observation.Id, new ObservationAcceptance(image.Index, image.Term,
                    failures, !current || !validClock)),
                Streams = next.Streams.SetItem(streamId, frontier)
            };
        }

        return Decide(image with { Monitoring = next }, command, OperationOutcome.Committed, null);
    }

    private static bool WasAssigned(RevisionImage image, MonitoringObservation observation)
    {
        string key = RevisionReducer.VersionKey(observation.TargetId, observation.ConfigRevision);
        if (!image.Versions.TryGetValue(key, out AcceptedValue? configuration))
        {
            return false;
        }

        return !configuration.Deleted && configuration.Value.Enabled
            && configuration.Kind is DeletedEntityKind.Server or DeletedEntityKind.Container
            && configuration.Value.Generation == observation.CheckGeneration
            && configuration.Value.ObserverNodes.Contains(observation.ObserverNodeId);
    }

    private static RevisionImage Decide(RevisionImage image, RevisionCommand command, OperationOutcome outcome, string? reason)
    {
        CommandResult result = new(command.OperationId, RevisionCodec.Digest(command), outcome,
            image.Index, image.Term, image.Index, reason, null, null, 0);
        return image with { Results = image.Results.Add(command.OperationId, result) };
    }
}
