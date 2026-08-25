using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Dashboard.Models;

namespace UptimeHarbor.Application.Dashboard;

public interface IDashboardQuery
{
    Task<ServiceResult<DashboardSnapshotDto>> GetSnapshotAsync(
        string? search,
        CancellationToken cancellationToken = default);
}
