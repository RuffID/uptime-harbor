using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Application.Visibility;
using UptimeHarbor.Application.Visibility.Models;

namespace UptimeHarbor.Infrastructure.Persistence.Queries;

public class MonitoringRecipientReadQuery(UptimeHarborDbContext dbContext)
    : IMonitoringRecipientReadQuery
{
    public async Task<IReadOnlyList<MonitoringRecipientReadRecord>> GetCandidatesAsync(
        MonitoringObjectKind objectKind,
        CancellationToken cancellationToken = default)
    {
        string normalizedAdministratorRole = ApplicationRoles.ADMINISTRATOR.ToUpperInvariant();
        var users = await dbContext.Users
            .AsNoTracking()
            .Select(user => new
            {
                user.Id,
                user.IsEnabled,
                IsAdministrator = dbContext.UserRoles.Any(userRole =>
                    userRole.UserId == user.Id
                    && dbContext.Roles.Any(role =>
                        role.Id == userRole.RoleId
                        && role.NormalizedName == normalizedAdministratorRole))
            })
            .ToListAsync(cancellationToken);
        List<Guid> ordinaryUserIds = users
            .Where(user => user.IsEnabled && !user.IsAdministrator)
            .Select(user => user.Id)
            .ToList();

        var serverPermissions = await dbContext.ServerVisibilityPermissions
            .AsNoTracking()
            .Where(permission => ordinaryUserIds.Contains(permission.UserId))
            .Select(permission => new
            {
                permission.UserId,
                permission.ServerId
            })
            .ToListAsync(cancellationToken);
        ILookup<Guid, Guid> serverPermissionsByUser = serverPermissions
            .ToLookup(permission => permission.UserId, permission => permission.ServerId);
        ILookup<Guid, Guid> containerPermissionsByUser;
        if (objectKind == MonitoringObjectKind.Container)
        {
            var containerPermissions = await dbContext.ContainerVisibilityPermissions
                .AsNoTracking()
                .Where(permission => ordinaryUserIds.Contains(permission.UserId))
                .Select(permission => new
                {
                    permission.UserId,
                    permission.ContainerId
                })
                .ToListAsync(cancellationToken);
            containerPermissionsByUser = containerPermissions
                .ToLookup(permission => permission.UserId, permission => permission.ContainerId);
        }
        else
        {
            containerPermissionsByUser = Array.Empty<(Guid UserId, Guid ContainerId)>()
                .ToLookup(permission => permission.UserId, permission => permission.ContainerId);
        }

        return users
            .Select(user => new MonitoringRecipientReadRecord(
                user.Id,
                user.IsEnabled,
                user.IsAdministrator,
                serverPermissionsByUser[user.Id].ToList(),
                containerPermissionsByUser[user.Id].ToList()))
            .ToList();
    }
}
