using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Dashboard;
using UptimeHarbor.Application.Dashboard.Models;
using UptimeHarbor.Web.Http;

namespace UptimeHarbor.Web.Controllers;

[ApiController]
[Route("api/dashboard")]
[Authorize]
public class DashboardController(IDashboardQuery dashboardQuery) : ControllerBase
{
    [HttpGet]
    public async Task<JsonResult> GetSnapshotAsync(
        [FromQuery] string? search,
        CancellationToken cancellationToken)
    {
        ServiceResult<DashboardSnapshotDto> result =
            await dashboardQuery.GetSnapshotAsync(search, cancellationToken);
        return JsonResultMapper.ToJsonResult(result);
    }
}
