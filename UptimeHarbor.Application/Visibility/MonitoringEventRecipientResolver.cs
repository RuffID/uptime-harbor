using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Visibility.Models;

namespace UptimeHarbor.Application.Visibility;

public class MonitoringEventRecipientResolver(
    IMonitoringRecipientReadQuery recipientReadQuery)
    : IMonitoringEventRecipientResolver
{
    public async Task<IReadOnlyList<Guid>> GetRecipientUserIdsAsync(
        MonitoringObjectKind objectKind,
        Guid objectId,
        Guid? serverId,
        CancellationToken cancellationToken = default)
    {
        if (objectKind == MonitoringObjectKind.Container && serverId is null)
        {
            throw new InvalidOperationException(
                "Событие контейнера должно содержать идентификатор родительского сервера.");
        }

        if (objectKind is not MonitoringObjectKind.Server and not MonitoringObjectKind.Container)
        {
            throw new ArgumentOutOfRangeException(
                nameof(objectKind),
                objectKind,
                "Неизвестный тип объекта мониторинга.");
        }

        IReadOnlyList<MonitoringRecipientReadRecord> candidates =
            await recipientReadQuery.GetCandidatesAsync(objectKind, cancellationToken);
        List<Guid> recipients = [];

        foreach (MonitoringRecipientReadRecord candidate in candidates)
        {
            if (!candidate.IsEnabled)
            {
                continue;
            }

            if (candidate.IsAdministrator)
            {
                recipients.Add(candidate.UserId);
                continue;
            }

            HashSet<Guid> permittedServerIds = candidate.PermittedServerIds.ToHashSet();

            bool canAccess = objectKind switch
            {
                MonitoringObjectKind.Server => ObjectVisibilityPolicy.CanAccessServer(
                    false,
                    objectId,
                    permittedServerIds),
                MonitoringObjectKind.Container => ObjectVisibilityPolicy.CanAccessContainer(
                    false,
                    serverId!.Value,
                    objectId,
                    permittedServerIds,
                    candidate.PermittedContainerIds.ToHashSet()),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(objectKind),
                    objectKind,
                    "Неизвестный тип объекта мониторинга.")
            };

            if (canAccess)
            {
                recipients.Add(candidate.UserId);
            }
        }

        return recipients;
    }
}
