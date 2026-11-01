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
        return users
            .Select(user => new MonitoringRecipientReadRecord(
                user.Id,
                user.IsEnabled,
                user.IsAdministrator,
                [],
                []))
            .ToList();
    }
}
