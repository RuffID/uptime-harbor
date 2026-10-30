using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Application.Users;
using UptimeHarbor.Application.Users.Models;
using UptimeHarbor.Web.Controllers.Models;
using UptimeHarbor.Web.Http;
using Microsoft.Extensions.Options;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Web.Controllers;

[ApiController]
[Route("api/admin/users")]
[Authorize(Roles = ApplicationRoles.ADMINISTRATOR)]
[ServiceFilter(typeof(UptimeHarbor.Web.Security.ClusterLegacyWriterFilter))]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class AdminUsersController(IUserManagementService userManagementService,
    SharedUserManagementService sharedUsers, IOptions<ClusterOptions> cluster) : ControllerBase
{
    [HttpGet]
    public async Task<JsonResult> GetAsync(CancellationToken cancellationToken)
    {
        if (cluster.Value.Enabled)
        {
            return JsonResultMapper.ToJsonResult(sharedUsers.GetUsers());
        }

        Application.Common.ServiceResult<IReadOnlyList<UserAccountDto>> result =
            await userManagementService.GetUsersAsync(cancellationToken);
        return JsonResultMapper.ToJsonResult(result);
    }

    [HttpPost("commands")]
    [ValidateAntiForgeryToken]
    public async Task<JsonResult> SubmitAsync(AccountChangeRequest request, CancellationToken cancellationToken)
    {
        if (!cluster.Value.Enabled)
        {
            return JsonResultMapper.ToJsonResult(Application.Common.ServiceResult.Fail(
                Application.Common.ServiceErrorType.Conflict, "Общие команды доступны только в режиме сетки."));
        }

        Application.Common.ServiceResult<AccountOperationDto> result = await sharedUsers.SubmitAsync(
            request, cluster.Value.NodeId, cancellationToken);
        JsonResult response = JsonResultMapper.ToJsonResult(result);
        if (result.Success && result.Data!.Status == "Pending")
        {
            response.StatusCode = StatusCodes.Status202Accepted;
        }

        return response;
    }

    [HttpGet("operations/{operationId:guid}")]
    public JsonResult GetOperation(Guid operationId)
    {
        return JsonResultMapper.ToJsonResult(sharedUsers.Find(operationId));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<JsonResult> CreateAsync(
        CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        Application.Common.ServiceResult<UserAccountDto> result =
            await userManagementService.CreateUserAsync(
                request.Login,
                request.InitialPassword,
                cancellationToken);
        return JsonResultMapper.ToJsonResult(result);
    }

    [HttpPut("{userId:guid}/enabled")]
    [ValidateAntiForgeryToken]
    public async Task<JsonResult> SetEnabledAsync(
        Guid userId,
        SetUserEnabledRequest request,
        CancellationToken cancellationToken)
    {
        Application.Common.ServiceResult result = await userManagementService.SetEnabledAsync(
            userId,
            request.IsEnabled,
            cancellationToken);
        return JsonResultMapper.ToJsonResult(result);
    }

    [HttpDelete("{userId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<JsonResult> DeleteAsync(Guid userId, CancellationToken cancellationToken)
    {
        Application.Common.ServiceResult result =
            await userManagementService.DeleteAsync(userId, cancellationToken);
        return JsonResultMapper.ToJsonResult(result);
    }
}
