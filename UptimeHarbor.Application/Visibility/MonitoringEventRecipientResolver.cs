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

        IReadOnlyList<UserAccountRecord> users =
            await userAccountStore.GetAllAsync(cancellationToken);
        List<UserAccountRecord> enabledUsers = users
            .Where(user => user.IsEnabled)
            .ToList();
        List<Guid> ordinaryUserIds = enabledUsers
            .Where(user => !user.IsAdministrator)
            .Select(user => user.Id)
            .ToList();
        List<ServerVisibilityPermission> serverPermissions =
            await unitOfWork.VisibilityPermissions.GetServerPermissionsForUsersAsync(
                ordinaryUserIds,
                cancellationToken);
        ILookup<Guid, ServerVisibilityPermission> serverPermissionsByUser =
            serverPermissions.ToLookup(permission => permission.UserId);
        ILookup<Guid, ContainerVisibilityPermission>? containerPermissionsByUser = null;
        if (objectKind == MonitoringObjectKind.Container)
        {
            List<ContainerVisibilityPermission> containerPermissions =
                await unitOfWork.VisibilityPermissions.GetContainerPermissionsForUsersAsync(
                    ordinaryUserIds,
                    cancellationToken);
            containerPermissionsByUser = containerPermissions.ToLookup(permission => permission.UserId);
        }

        List<Guid> recipients = [];

        foreach (UserAccountRecord user in enabledUsers)
        {
            if (user.IsAdministrator)
            {
                recipients.Add(user.Id);
                continue;
            }

            HashSet<Guid> permittedServerIds = serverPermissionsByUser[user.Id]
                .Select(permission => permission.ServerId)
                .ToHashSet();

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
                    containerPermissionsByUser![user.Id]
                        .Select(permission => permission.ContainerId)
                        .ToHashSet()),
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
}
