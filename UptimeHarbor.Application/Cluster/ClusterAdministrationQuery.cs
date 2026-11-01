using System.Text.Json;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

public class ClusterAdministrationQuery(ClusterAdministrationAccess access, RevisionStateStore state,
    IUserAccessContext user, TimeProvider time)
{
    public ServiceResult<AdministrationSnapshotDto> Read(Guid nodeId)
    {
        ServiceError? error = access.GetError();
        if (error is not null)
        {
            return ServiceResult<AdministrationSnapshotDto>.Fail(error);
        }

        RevisionImage image = state.Current!;
        DateTimeOffset now = time.GetUtcNow();
        AdministrationEntityDto[] entities = image.Values.Values
            .Where(value => value.Kind is DeletedEntityKind.Server or DeletedEntityKind.Container
                or DeletedEntityKind.Chat or DeletedEntityKind.Binding or DeletedEntityKind.Account or DeletedEntityKind.NodeChatRoute)
            .OrderBy(value => value.Kind).ThenBy(value => value.Id).Select(AdministrationMapping.Entity).ToArray();
        AdministrationConflictDto[] conflicts = image.Conflicts.Values.OrderByDescending(conflict => conflict.Index)
            .Select(conflict => MapConflict(image, conflict)).ToArray();
        AdministrationTargetDto[] targets = entities.Where(value => !value.Deleted
            && value.Kind is "Server" or "Container").Select(value => value.Id).Concat(image.Monitoring.Nodes.Keys)
            .Distinct().Order().Select(id => AdministrationMonitoring.Read(image, id, now)).ToArray();
        AdministrationDeliveryDto[] deliveries = image.Monitoring.Intents.Values.OrderByDescending(intent => intent.Index)
            .Select(intent => MapDelivery(image, intent)).ToArray();
        return ServiceResult<AdministrationSnapshotDto>.Ok(new(nodeId, user.UserId, image.Index, now,
            entities, image.Monitoring.Nodes.Keys.Order().ToArray(), conflicts, targets, deliveries));
    }

    private static AdministrationConflictDto MapConflict(RevisionImage image, ConflictVariant conflict)
    {
        RevisionCommand proposal = RevisionCodec.Decode(System.Text.Encoding.UTF8.GetBytes(conflict.ProposedPayload));
        AcceptedValue? baseline = JsonSerializer.Deserialize<AcceptedValue>(conflict.BasePayload);
        AcceptedValue? first = JsonSerializer.Deserialize<AcceptedValue>(conflict.AcceptedPayload);
        AcceptedValue? current = image.Values.GetValueOrDefault(conflict.EntityId);
        AdministrationEntityDto? baselineDto = null;
        AdministrationEntityDto? firstDto = null;
        AdministrationEntityDto? currentDto = null;
        AdministrationSettingsDto? proposed = null;
        if (baseline is not null)
        {
            baselineDto = AdministrationMapping.Entity(baseline);
        }

        if (first is not null)
        {
            firstDto = AdministrationMapping.Entity(first);
        }

        if (current is not null)
        {
            currentDto = AdministrationMapping.Entity(current);
        }

        if (proposal.Value is not null)
        {
            proposed = AdministrationMapping.Settings(proposal.EntityKind, proposal.Value);
        }

        return new(conflict.Id, conflict.EntityId, proposal.EntityKind.ToString(), proposal.Action,
            proposal.AccountAction, conflict.BaseRevision, current?.Revision ?? 0, baselineDto,
            firstDto, proposed, currentDto, conflict.ResolutionOperationId, conflict.ResolvedAtUtc);
    }

    private static AdministrationDeliveryDto MapDelivery(RevisionImage image, MonitoringIntent intent)
    {
        AcceptedDeliveryAttempt[] attempts = image.Monitoring.Attempts.Values.Where(attempt => attempt.IntentId == intent.Id)
            .OrderBy(attempt => attempt.Number).ToArray();
        string? failure = attempts.LastOrDefault()?.FailureKind;
        if (failure is not null)
        {
            DeliveryRetry.ValidateFailure(failure);
            failure = failure.Split('/')[0];
        }

        return new(intent.Id, intent.ChatId, intent.IncidentId, intent.Kind.ToString(), intent.State.ToString(),
            attempts.Length, failure, intent.CompletedAtUtc);
    }
}
