using UptimeHarbor.Application.Dashboard.Models;

namespace UptimeHarbor.Application.Dashboard;

public interface IDashboardSnapshotReadQuery
{
    Task<DashboardReadSnapshot> GetSnapshotAsync(
        Guid userId,
        bool isAdministrator,
        CancellationToken cancellationToken = default);
}
