using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Users;
using UptimeHarbor.Application.Users.Models;
using UptimeHarbor.Domain.Access;

namespace UptimeHarbor.Application.Visibility;

public class MonitoringEventRecipientResolver(
    IUserAccountStore userAccountStore,
    IVisibilitySettingsUnitOfWork unitOfWork)
    : IMonitoringEventRecipientResolver
{
    public async Task<IReadOnlyList<Guid>> GetRecipientUserIdsAsync(
        MonitoringObjectKind objectKind,
        Guid objectId,
        Guid? serverId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<UserAccountRecord> users =
            await userAccountStore.GetAllAsync(cancellationToken);
        List<Guid> recipients = [];

        foreach (UserAccountRecord user in users.Where(user => user.IsEnabled))
        {
            if (user.IsAdministrator)
            {
                recipients.Add(user.Id);
                continue;
            }

            List<ServerVisibilityPermission> serverPermissions =
                await unitOfWork.VisibilityPermissions.GetServerPermissionsAsync(
                    user.Id,
                    cancellationToken);
            HashSet<Guid> permittedServerIds = serverPermissions
                .Select(permission => permission.ServerId)
                .ToHashSet();

            bool canAccess = objectKind switch
            {
                MonitoringObjectKind.Server => ObjectVisibilityPolicy.CanAccessServer(
                    false,
                    objectId,
                    permittedServerIds),
                MonitoringObjectKind.Container => await CanAccessContainerAsync(
                    user.Id,
                    objectId,
                    serverId,
                    permittedServerIds,
                    cancellationToken),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(objectKind),
                    objectKind,
                    "Неизвестный тип объекта мониторинга.")
            };

            if (canAccess)
            {
                recipients.Add(user.Id);
            }
        }

        return recipients;
    }

    private async Task<bool> CanAccessContainerAsync(
        Guid userId,
        Guid containerId,
        Guid? serverId,
        IReadOnlySet<Guid> permittedServerIds,
        CancellationToken cancellationToken)
    {
        if (serverId is null)
        {
            throw new InvalidOperationException(
                "Событие контейнера должно содержать идентификатор родительского сервера.");
        }

        List<ContainerVisibilityPermission> containerPermissions =
            await unitOfWork.VisibilityPermissions.GetContainerPermissionsAsync(
                userId,
                cancellationToken);
        HashSet<Guid> permittedContainerIds = containerPermissions
            .Select(permission => permission.ContainerId)
            .ToHashSet();
        return ObjectVisibilityPolicy.CanAccessContainer(
            false,
            serverId.Value,
            containerId,
            permittedServerIds,
            permittedContainerIds);
    }
}
