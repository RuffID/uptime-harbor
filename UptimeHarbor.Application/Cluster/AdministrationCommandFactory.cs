using System.Collections.Immutable;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

public static class AdministrationCommandFactory
{
    public static RevisionCommand Prepare(ConfigurationChangeRequest request, RevisionImage image,
        DateTimeOffset now)
    {
        if (request.OperationId == Guid.Empty || request.EntityId == Guid.Empty || request.BaseRevision < 0
            || !Enum.TryParse(request.Kind, out DeletedEntityKind kind) || request.Kind != kind.ToString()
            || kind is not (DeletedEntityKind.Server or DeletedEntityKind.Container or DeletedEntityKind.Chat
                or DeletedEntityKind.Binding or DeletedEntityKind.NodeChatRoute)
            || request.Action is not ("Upsert" or "Delete" or "TestDelivery")
            || request.TargetOperationId.HasValue != request.ControlOperationId.HasValue
            || request.TargetOperationId == Guid.Empty || request.ControlOperationId == Guid.Empty
            || request.ControlOperationId == request.OperationId || request.TargetOperationId == request.OperationId
            || request.TargetOperationId.HasValue && request.TargetOperationId == request.ControlOperationId
            || request.ConfirmRestored && !request.TargetOperationId.HasValue
            || request.Action == "Delete" && !request.ConfirmGlobalDelete)
        {
            throw new ArgumentException("InvalidAdministrationCommand");
        }

        if ((request.Action == "Upsert") != (request.Settings is not null)
            || request.Action == "TestDelivery" && (kind != DeletedEntityKind.Chat || request.BaseRevision != 0
                || request.ResolvesConflictId.HasValue || request.TargetOperationId.HasValue))
        {
            throw new ArgumentException("InvalidAdministrationValue");
        }

        image.Values.TryGetValue(request.EntityId, out AcceptedValue? current);
        ConfigurationValue? value = null;
        if (request.Settings is not null)
        {
            value = PrepareValue(kind, request.Settings, current);
        }

        RevisionCommand command = new()
        {
            ClusterId = image.ClusterId, ClusterGeneration = image.Generation, OperationId = request.OperationId,
            EntityId = request.EntityId, EntityKind = kind, BaseRevision = request.BaseRevision,
            Action = request.Action, ResolvesConflictId = request.ResolvesConflictId, Value = value,
            OriginNodeId = Guid.Empty, ActorUserId = Guid.Empty, ActorRevision = 0, CreatedAtUtc = now
        };
        if (request.Action == "Upsert" && request.BaseRevision == (current?.Revision ?? 0))
        {
            AcceptedValue candidate = new(request.EntityId, kind, Math.Max(1, image.Index), value!);
            string? invalid = RevisionValidation.Validate(image, command, candidate, current);
            if (invalid is not null && invalid != "DuplicateKey")
            {
                throw new ArgumentException("InvalidAdministrationValue");
            }
        }

        return command;
    }

    public static bool Matches(ConfigurationChangeRequest request, RevisionCommand command)
    {
        if (request.OperationId != command.OperationId || request.EntityId != command.EntityId
            || request.Kind != command.EntityKind.ToString() || request.BaseRevision != command.BaseRevision
            || request.Action != command.Action || request.ResolvesConflictId != command.ResolvesConflictId)
        {
            return false;
        }

        if (request.Settings is null || command.Value is null)
        {
            return request.Settings is null && command.Value is null;
        }

        ConfigurationValue candidate = PrepareValue(command.EntityKind, request.Settings, null)
            with { Generation = command.Value.Generation };
        return RevisionCodec.Digest(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(candidate))
            == RevisionCodec.Digest(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(command.Value));
    }

    private static ConfigurationValue PrepareValue(DeletedEntityKind kind, AdministrationSettingsDto settings,
        AcceptedValue? current)
    {
        if (settings.ObserverNodes is null)
        {
            throw new ArgumentException("InvalidObservers");
        }

        ConfigurationValue value = new()
        {
            Name = settings.Name?.Trim(), Endpoint = settings.Endpoint?.Trim(), Description = settings.Description?.Trim(),
            Enabled = settings.Enabled, ParentId = settings.ParentId, ChatId = settings.ChatId,
            TelegramChatId = settings.TelegramChatId, ObserverNodes = settings.ObserverNodes.Order().ToImmutableArray(),
            FailureThreshold = settings.FailureThreshold, IntervalSeconds = settings.IntervalSeconds,
            TimeoutSeconds = settings.TimeoutSeconds
        };
        if (string.IsNullOrEmpty(value.Description))
        {
            value = value with { Description = null };
        }

        Guid generation = Guid.NewGuid();
        if (current is not null)
        {
            ConfigurationValue old = current.Value;
            bool sameChecks = old.Endpoint == value.Endpoint && old.Enabled == value.Enabled
                && old.ObserverNodes.Order().SequenceEqual(value.ObserverNodes)
                && old.FailureThreshold == value.FailureThreshold && old.IntervalSeconds == value.IntervalSeconds
                && old.TimeoutSeconds == value.TimeoutSeconds;
            if (sameChecks || kind is not (DeletedEntityKind.Server or DeletedEntityKind.Container))
            {
                generation = old.Generation;
            }
        }

        return value with { Generation = generation };
    }
}
