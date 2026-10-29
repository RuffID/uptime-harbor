using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Domain;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

public static class MonitoringCommandValidation
{
    public static bool IsSystem(RevisionCommand command)
    {
        return command.Action is "RecordObservations" or "EvaluateAvailability" or "PlanBacklogDelivery";
    }

    public static void Validate(RevisionCommand command)
    {
        MonitoringCommand payload = command.Monitoring ?? throw new InvalidDataException("MonitoringPayloadMissing");
        bool observer = command.Action == "RecordObservations";
        string source = "MonitoringCoordinator";
        if (observer)
        {
            source = "MonitoringObserver";
        }

        if (command.SystemSource != source || command.ActorUserId != Guid.Empty || command.ActorRevision != 0
            || command.Value is not null || command.Target is not null || command.Replacement is not null
            || command.AccountAction is not null || command.ResolvesConflictId.HasValue
            || payload.StorageGeneration == Guid.Empty || command.EntityKind is not (DeletedEntityKind.Server
                or DeletedEntityKind.Container or DeletedEntityKind.Node))
        {
            throw new InvalidDataException("InvalidMonitoringProvenance");
        }

        if (observer)
        {
            if (payload.Observations.IsDefaultOrEmpty || payload.Observations.Length > 100
                || payload.CoordinatorTerm != 0 || payload.EvaluationTimeUtc.HasValue || payload.CutIndex != 0)
            {
                throw new InvalidDataException("InvalidObservationBatch");
            }

            foreach (MonitoringObservation observation in payload.Observations)
            {
                ValidateObservation(observation);
                if (observation.TargetId != command.EntityId || observation.ObserverNodeId != command.OriginNodeId
                    || observation.StorageGeneration != payload.StorageGeneration)
                {
                    throw new InvalidDataException("ObservationOriginMismatch");
                }
            }
        }
        else
        {
            if (payload.CoordinatorTerm <= 0 || !payload.Observations.IsEmpty)
            {
                throw new InvalidDataException("InvalidCoordinatorCommand");
            }

            if (command.Action == "EvaluateAvailability")
            {
                StorageGuards.Utc(payload.EvaluationTimeUtc ?? throw new InvalidDataException("EvaluationTimeMissing"));
                StorageGuards.Digest(payload.FrontierDigest!);
            }
            else if (payload.CutIndex <= 0)
            {
                throw new InvalidDataException("InvalidBacklogCut");
            }
        }
    }

    public static void ValidateObservation(MonitoringObservation observation)
    {
        StorageGuards.Id(observation.Id, nameof(observation.Id));
        StorageGuards.Id(observation.TargetId, nameof(observation.TargetId));
        StorageGuards.Id(observation.ObserverNodeId, nameof(observation.ObserverNodeId));
        StorageGuards.Id(observation.StorageGeneration, nameof(observation.StorageGeneration));
        StorageGuards.Id(observation.CheckGeneration, nameof(observation.CheckGeneration));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(observation.ConfigRevision);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(observation.Sequence);
        ArgumentOutOfRangeException.ThrowIfNegative(observation.DurationMilliseconds);
        StorageGuards.Utc(observation.CheckedAtUtc);
        if (observation.StartedAtUtc.HasValue)
        {
            StorageGuards.Utc(observation.StartedAtUtc.Value);
            if (observation.StartedAtUtc > observation.CheckedAtUtc)
            {
                throw new InvalidDataException("InvalidEndpointStart");
            }
        }

        bool knownFailure = Enum.TryParse(observation.FailureKind, out EndpointCheckFailureKind parsed) && Enum.IsDefined(parsed);
        knownFailure |= observation.FailureKind is "PeerUnavailable" or "DatabaseUnavailable" or "WorkerStalled" or "TelegramUnavailable";
        if (observation.Succeeded)
        {
            if (observation.FailureKind is not null)
            {
                throw new InvalidDataException("UnexpectedFailureKind");
            }

            if (observation.EndpointName is not null)
            {
                StorageGuards.Text(observation.EndpointName, ModelConstraints.OBJECT_NAME_MAX_LENGTH, nameof(observation.EndpointName));
            }
        }
        else if (!knownFailure || observation.EndpointName is not null || observation.StartedAtUtc.HasValue)
        {
            throw new InvalidDataException("InvalidObservationFailure");
        }
    }
}
