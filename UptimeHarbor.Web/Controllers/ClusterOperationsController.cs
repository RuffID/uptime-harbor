using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Web.Http;

namespace UptimeHarbor.Web.Controllers;

[ApiController]
[Route("api/admin/cluster/lifecycle")]
[Authorize(Roles = ApplicationRoles.ADMINISTRATOR)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class ClusterOperationsController(ClusterOperationsAdministration administration, IOptions<ClusterOptions> options) : ControllerBase
{
    [HttpGet]
    public JsonResult Read()
    {
        if (!options.Value.Enabled)
        {
            return JsonResultMapper.ToJsonResult(ServiceResult.Fail(ServiceErrorType.Conflict, "Режим сетки выключен."));
        }

        return JsonResultMapper.ToJsonResult(administration.Read());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<JsonResult> SubmitAsync(ClusterOperationRequest request, CancellationToken token)
    {
        if (!options.Value.Enabled)
        {
            return JsonResultMapper.ToJsonResult(ServiceResult.Fail(ServiceErrorType.Conflict, "Режим сетки выключен."));
        }

        ServiceResult<AdministrationOperationDto> result = await administration.SubmitAsync(request, token);
        JsonResult response = JsonResultMapper.ToJsonResult(result);
        if (result.Success && result.Data!.Status == "LocalPending")
        {
            response.StatusCode = StatusCodes.Status202Accepted;
        }

        return response;
    }
}
