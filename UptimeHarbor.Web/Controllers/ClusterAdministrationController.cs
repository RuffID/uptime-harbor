using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Web.Cluster;
using UptimeHarbor.Web.Controllers.Models;
using UptimeHarbor.Web.Http;

namespace UptimeHarbor.Web.Controllers;

[ApiController]
[Route("api/admin/cluster")]
[Authorize(Roles = ApplicationRoles.ADMINISTRATOR)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class ClusterAdministrationController(ClusterAdministrationQuery query, ClusterPendingQuery pending,
    ClusterConfigurationCommands commands, ClusterBrowserDiagnostics diagnostics, IOptions<ClusterOptions> options) : ControllerBase
{
    [HttpGet]
    public JsonResult Read()
    {
        return JsonResultMapper.ToJsonResult(query.Read(options.Value.NodeId));
    }

    [HttpGet("pending")]
    public async Task<JsonResult> ReadPendingAsync([FromQuery] int offset, CancellationToken token)
    {
        return JsonResultMapper.ToJsonResult(await pending.ReadAsync(offset, token));
    }

    [HttpGet("operations/{operationId:guid}")]
    public JsonResult Find(Guid operationId)
    {
        return JsonResultMapper.ToJsonResult(pending.Find(operationId));
    }

    [HttpGet("diagnostics")]
    public JsonResult Diagnostics()
    {
        return JsonResultMapper.ToJsonResult(diagnostics.Read());
    }

    [HttpPost("commands")]
    [ValidateAntiForgeryToken]
    public async Task<JsonResult> SubmitAsync(ConfigurationChangeRequest request, CancellationToken token)
    {
        if (!options.Value.Enabled)
        {
            return Disabled();
        }

        return OperationResponse(await commands.SubmitAsync(request, options.Value.NodeId, token));
    }

    [HttpPost("pending/{operationId:guid}/cancel")]
    [ValidateAntiForgeryToken]
    public async Task<JsonResult> CancelAsync(Guid operationId, CancelPendingRequest request, CancellationToken token)
    {
        if (!options.Value.Enabled)
        {
            return Disabled();
        }

        return OperationResponse(await commands.CancelAsync(operationId, request.OperationId, options.Value.NodeId, token));
    }

    private static JsonResult OperationResponse(ServiceResult<AdministrationOperationDto> result)
    {
        JsonResult response = JsonResultMapper.ToJsonResult(result);
        if (result.Success && result.Data!.Status == "LocalPending")
        {
            response.StatusCode = StatusCodes.Status202Accepted;
        }

        return response;
    }

    private static JsonResult Disabled()
    {
        return JsonResultMapper.ToJsonResult(ServiceResult.Fail(ServiceErrorType.Conflict, "Режим сетки выключен."));
    }
}
