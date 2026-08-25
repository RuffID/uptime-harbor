using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UptimeHarbor.Application.Visibility;
using UptimeHarbor.Application.Visibility.Models;
using UptimeHarbor.Web.Http;

namespace UptimeHarbor.Web.Controllers;

[ApiController]
[Route("api/objects")]
[Authorize]
public class ObjectsController(IAccessibleObjectsService accessibleObjectsService) : ControllerBase
{
    [HttpGet]
    public async Task<JsonResult> GetAllAsync(CancellationToken cancellationToken)
    {
        Application.Common.ServiceResult<IReadOnlyList<AccessibleServerDto>> result =
            await accessibleObjectsService.GetAllAsync(cancellationToken);
        return JsonResultMapper.ToJsonResult(result);
    }

    [HttpGet("servers/{serverId:guid}")]
    public async Task<JsonResult> GetServerAsync(Guid serverId, CancellationToken cancellationToken)
    {
        Application.Common.ServiceResult<AccessibleServerDto> result =
            await accessibleObjectsService.GetServerAsync(serverId, cancellationToken);
        return JsonResultMapper.ToJsonResult(result);
    }

    [HttpGet("containers/{containerId:guid}")]
    public async Task<JsonResult> GetContainerAsync(Guid containerId, CancellationToken cancellationToken)
    {
        Application.Common.ServiceResult<AccessibleContainerDto> result =
            await accessibleObjectsService.GetContainerAsync(containerId, cancellationToken);
        return JsonResultMapper.ToJsonResult(result);
    }
}
