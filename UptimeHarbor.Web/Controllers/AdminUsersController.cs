using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Application.Users;
using UptimeHarbor.Application.Users.Models;
using UptimeHarbor.Web.Controllers.Models;
using UptimeHarbor.Web.Http;

namespace UptimeHarbor.Web.Controllers;

[ApiController]
[Route("api/admin/users")]
[Authorize(Roles = ApplicationRoles.ADMINISTRATOR)]
public class AdminUsersController(IUserManagementService userManagementService) : ControllerBase
{
    [HttpGet]
    public async Task<JsonResult> GetAsync(CancellationToken cancellationToken)
    {
        Application.Common.ServiceResult<IReadOnlyList<UserAccountDto>> result =
            await userManagementService.GetUsersAsync(cancellationToken);
        return JsonResultMapper.ToJsonResult(result);
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
