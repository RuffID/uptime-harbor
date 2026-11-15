using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Issuers;
using UptimeHarbor.Web.Controllers.Models;
using UptimeHarbor.Web.Http;

namespace UptimeHarbor.Web.Controllers;

[ApiController]
[Route("api/admin/cluster/enrollment-issuers")]
[Authorize(Roles = ApplicationRoles.ADMINISTRATOR)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(4096)]
public class ClusterEnrollmentIssuersController(EnrollmentIssuerAdministration administration) : ControllerBase
{
    [HttpPost]
    [EnableRateLimiting("onboarding-administrator")]
    [ValidateAntiForgeryToken]
    public async Task<JsonResult> RegisterAsync(RegisterEnrollmentIssuerRequest request, CancellationToken token)
    {
        return Map(await administration.RegisterAsync(request.OperationId, request.Password, token));
    }

    [HttpGet("operations/{operationId:guid}")]
    public async Task<JsonResult> ReadAsync(Guid operationId, CancellationToken token)
    {
        return Map(await administration.ReadAsync(operationId, token));
    }

    private static JsonResult Map(ServiceResult<EnrollmentIssuerStatus> result)
    {
        JsonResult response = JsonResultMapper.ToJsonResult(result);
        if (result.Success && result.Data!.OperationStatus == "LocalPending")
        {
            response.StatusCode = StatusCodes.Status202Accepted;
        }

        return response;
    }
}
