using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Application.Dashboard;
using UptimeHarbor.Application.Dashboard.Models;
using UptimeHarbor.Domain.Monitoring;

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
            .Where(server => server.Type == ServerType.Server)
            .Select(server => new DashboardServerReadRecord(
                server.Id,
                server.Name,
                server.Description,
                server.LastKnownName,
                server.Status,
                server.LastCheckedAtUtc,
                server.StartedAtUtc))
            .ToListAsync(cancellationToken);
        List<DashboardContainerReadRecord> containers = await dbContext.MonitoredServers
            .AsNoTracking()
            .Where(container => container.Type == ServerType.Application)
            .Select(container => new DashboardContainerReadRecord(
                container.Id,
                container.ServerId,
                container.Description,
                container.LastKnownName,
                container.Status,
                container.LastCheckedAtUtc,
                container.StartedAtUtc,
                container.Name))
            .ToListAsync(cancellationToken);

        if (isAdministrator)
        {
            return new DashboardReadSnapshot(servers, containers, [], []);
        }

        return new DashboardReadSnapshot([], [], [], []);
    }
}
