using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

public static class RevisionCodec
{
    public const int COMMAND_ID = 1;
    public const int MAX_COMMAND_BYTES = 262144;
    private static readonly JsonSerializerOptions OPTIONS = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public static byte[] Encode(RevisionCommand command)
    {
        Validate(command);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(command, OPTIONS);
        if (bytes.Length > MAX_COMMAND_BYTES)
        {
            throw new InvalidDataException("CommandTooLarge");
        }

        return bytes;
    }

    public static RevisionCommand Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MAX_COMMAND_BYTES)
        {
            throw new InvalidDataException("InvalidCommandLength");
        }

        RevisionCommand command = JsonSerializer.Deserialize<RevisionCommand>(bytes, OPTIONS)
            ?? throw new InvalidDataException("InvalidCommand");
        Validate(command);
        return command;
    }

    public static string Digest(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static string Digest(RevisionCommand command)
    {
        return Digest(Encode(command));
    }

    public static void Validate(RevisionCommand command)
    {
        ValidateLeaf(command);
        if (command.Action is "Cancel" or "Replace")
        {
            RevisionCommand target = command.Target ?? throw new InvalidDataException("MissingControlTarget");
            ValidateLeaf(target);
            if (!CanControl(target) || target.Target is not null || target.Replacement is not null
                || target.ClusterId != command.ClusterId || target.ClusterGeneration != command.ClusterGeneration
                || target.OperationId == command.OperationId || target.EntityId != command.EntityId
                || target.EntityKind != command.EntityKind)
            {
                throw new InvalidDataException("InvalidControlTarget");
            }

            if (command.Action == "Replace")
            {
                RevisionCommand replacement = command.Replacement ?? throw new InvalidDataException("MissingReplacement");
                ValidateLeaf(replacement);
                if (!CanControl(replacement) || replacement.Target is not null
                    || replacement.Replacement is not null || replacement.OperationId == target.OperationId
                    || replacement.OperationId == command.OperationId || replacement.EntityId != target.EntityId
                    || replacement.EntityKind != target.EntityKind || replacement.ClusterId != command.ClusterId
                    || replacement.ClusterGeneration != command.ClusterGeneration || replacement.ActorUserId != command.ActorUserId
                    || replacement.OriginNodeId != command.OriginNodeId)
                {
                    throw new InvalidDataException("InvalidReplacement");
                }
            }
            else if (command.Replacement is not null)
            {
                throw new InvalidDataException("UnexpectedReplacement");
            }
        }
        else if (command.Target is not null || command.Replacement is not null)
        {
            throw new InvalidDataException("UnexpectedControl");
        }
    }

    private static bool CanControl(RevisionCommand command)
    {
        return command.Action is "Upsert" or "Delete"
            || ClusterOperationValidation.IsOperation(command) && !ClusterOperationValidation.IsInternal(command);
    }

    private static void ValidateLeaf(RevisionCommand command)
    {
        bool delivery = DeliveryCommands.IsSystem(command);
        bool operation = ClusterOperationValidation.IsOperation(command);
        bool system = MonitoringCommandValidation.IsSystem(command) || delivery || operation;
        if (command.Schema != 1 || !system && command.Action is not ("Upsert" or "Delete" or "Cancel" or "Replace" or "BootstrapAccount" or "TestDelivery")
            || !Enum.IsDefined(command.EntityKind))
        {
            throw new InvalidDataException("UnsupportedCommand");
        }

        StorageGuards.Id(command.ClusterId, nameof(command.ClusterId));
        StorageGuards.Id(command.OperationId, nameof(command.OperationId));
        StorageGuards.Id(command.OriginNodeId, nameof(command.OriginNodeId));
        StorageGuards.Id(command.EntityId, nameof(command.EntityId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.ClusterGeneration);
        ArgumentOutOfRangeException.ThrowIfNegative(command.BaseRevision);
        StorageGuards.Utc(command.CreatedAtUtc);
        if (system)
        {
            if (operation)
            {
                ClusterOperationValidation.Validate(command);
                return;
            }

            if (delivery)
            {
                DeliveryCommands.Validate(command);
                return;
            }

            MonitoringCommandValidation.Validate(command);
            return;
        }

        StorageGuards.Id(command.ActorUserId, nameof(command.ActorUserId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.ActorRevision);
        if (command.SystemSource is not null || command.Monitoring is not null || command.Delivery is not null
            || command.Operation is not null)
        {
            throw new InvalidDataException("UnexpectedSystemSource");
        }
        if ((command.Action is "Upsert" or "BootstrapAccount") != (command.Value is not null))
        {
            throw new InvalidDataException("InvalidCommandValue");
        }

        if (command.Action == "TestDelivery" && (command.EntityKind != DeletedEntityKind.Chat || command.BaseRevision != 0
            || command.ResolvesConflictId.HasValue || command.AccountAction is not null))
        {
            throw new InvalidDataException("InvalidTestDelivery");
        }

        if (command.Action == "BootstrapAccount"
            && (command.EntityKind != DeletedEntityKind.Account || command.ActorUserId != command.EntityId
                || command.ActorRevision != 1 || command.BaseRevision != 0 || command.Value?.Enabled != true
                || command.ResolvesConflictId.HasValue))
        {
            throw new InvalidDataException("InvalidAccountBootstrap");
        }
    }
}
