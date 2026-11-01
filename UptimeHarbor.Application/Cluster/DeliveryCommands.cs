using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

public static class DeliveryCommands
{
    public static bool IsSystem(RevisionCommand command)
    {
        return command.SystemSource is "DeliveryCoordinator" or "DeliveryTransport";
    }

    public static Guid AttemptId(Guid intentId, int number)
    {
        return MonitoringKeys.Id($"attempt/{intentId:N}/{number}");
    }

    public static RevisionCommand Create(RevisionImage image, Guid origin, Guid storage, string action,
        Guid entityId, Guid operationId, DateTimeOffset atUtc, DeliveryCommand payload, bool transport = false)
    {
        string source = "DeliveryCoordinator";
        if (transport)
        {
            source = "DeliveryTransport";
        }

        return new RevisionCommand
        {
            ClusterId = image.ClusterId, ClusterGeneration = image.Generation, OriginNodeId = origin,
            OperationId = operationId, EntityId = entityId, EntityKind = DeletedEntityKind.Node,
            ActorUserId = Guid.Empty, ActorRevision = 0, BaseRevision = 0, Action = action,
            CreatedAtUtc = atUtc, SystemSource = source, Delivery = payload with { StorageGeneration = storage }
        };
    }

    public static void Validate(RevisionCommand command)
    {
        DeliveryCommand payload = command.Delivery ?? throw new InvalidDataException("DeliveryPayloadMissing");
        if (!IsSystem(command) || command.ActorUserId != Guid.Empty || command.ActorRevision != 0
            || command.Monitoring is not null || command.Value is not null || command.Target is not null
            || command.Replacement is not null || command.ResolvesConflictId.HasValue || command.AccountAction is not null
            || command.BaseRevision != 0 || command.EntityKind != DeletedEntityKind.Node)
        {
            throw new InvalidDataException("InvalidDeliverySource");
        }

        StorageGuards.Id(payload.StorageGeneration, nameof(payload.StorageGeneration));
        if (command.Action is "RenewSender" or "BeginDelivery" or "AbandonDelivery")
        {
            if (payload.Outcome != AttemptOutcome.Unknown || payload.CompletedAtUtc.HasValue
                || payload.TelegramMessageId.HasValue || payload.FailureKind is not null)
            {
                throw new InvalidDataException("UnexpectedSenderResult");
            }

            SenderAuthorityKey key = payload.Authority ?? throw new InvalidDataException("SenderAuthorityMissing");
            if (command.SystemSource != "DeliveryCoordinator" || key.ClusterId != command.ClusterId
                || key.Generation != command.ClusterGeneration || key.LeaderNodeId != command.OriginNodeId
                || key.Term <= 0 || key.MembershipVersion < 0 || key.SenderEpoch == Guid.Empty)
            {
                throw new InvalidDataException("SenderAuthorityMismatch");
            }

            if (command.Action == "RenewSender")
            {
                StorageGuards.Id(payload.RoundId, nameof(payload.RoundId));
            }
            else
            {
                StorageGuards.Id(payload.AttemptId, nameof(payload.AttemptId));
            }
        }
        else if (command.Action == "RecordDelivery")
        {
            StorageGuards.Id(payload.AttemptId, nameof(payload.AttemptId));
            if (command.SystemSource != "DeliveryTransport" || !payload.CompletedAtUtc.HasValue)
            {
                throw new InvalidDataException("InvalidDeliveryResult");
            }

            _ = new CapturedDeliveryResult(payload.AttemptId, command.OriginNodeId, payload.Outcome,
                payload.CompletedAtUtc.Value, payload.TelegramMessageId, payload.FailureKind);
            DeliveryRetry.ValidateFailure(payload.FailureKind);
            bool succeeded = payload.Outcome == AttemptOutcome.Succeeded;
            if (succeeded != (payload.FailureKind is null) || payload.TelegramMessageId <= 0
                || payload.Authority is not null || payload.RoundId != Guid.Empty
                || payload.CompletedAtUtc > command.CreatedAtUtc)
            {
                throw new InvalidDataException("InvalidDeliveryResult");
            }
        }
        else
        {
            throw new InvalidDataException("UnknownDeliveryAction");
        }
    }
}
