using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Application.Dashboard;
using UptimeHarbor.Application.Dashboard.Models;

namespace UptimeHarbor.Infrastructure.Persistence.Queries;

public class DashboardSnapshotReadQuery(UptimeHarborDbContext dbContext)
    : IDashboardSnapshotReadQuery
{
    public async Task<DashboardReadSnapshot> GetSnapshotAsync(
        Guid userId,
        bool isAdministrator,
        CancellationToken cancellationToken = default)
    {
        List<DashboardServerReadRecord> servers = await dbContext.MonitoredServers
            .AsNoTracking()
            .Select(server => new DashboardServerReadRecord(
                server.Id,
                server.Name,
                server.Description,
                server.LastKnownName,
                server.Status,
                server.LastCheckedAtUtc))
            .ToListAsync(cancellationToken);
        List<DashboardContainerReadRecord> containers = await dbContext.MonitoredContainers
            .AsNoTracking()
            .Select(container => new DashboardContainerReadRecord(
                container.Id,
                container.ServerId,
                container.Description,
                container.LastKnownName,
                container.Status,
                container.LastCheckedAtUtc,
                container.StartedAtUtc))
            .ToListAsync(cancellationToken);

        if (isAdministrator)
        {
            return new DashboardReadSnapshot(servers, containers, [], []);
        }

        List<Guid> permittedServerIds = await dbContext.ServerVisibilityPermissions
            .AsNoTracking()
            .Where(permission => permission.UserId == userId)
            .Select(permission => permission.ServerId)
            .ToListAsync(cancellationToken);
        List<Guid> permittedContainerIds = await dbContext.ContainerVisibilityPermissions
            .AsNoTracking()
            .Where(permission => permission.UserId == userId)
            .Where(permission => dbContext.MonitoredContainers.Any(container =>
                container.Id == permission.ContainerId && container.ServerId == permission.ServerId))
            .Select(permission => permission.ContainerId)
            .ToListAsync(cancellationToken);

        return new DashboardReadSnapshot(
            servers,
            containers,
            permittedServerIds,
            permittedContainerIds);
    }
}
