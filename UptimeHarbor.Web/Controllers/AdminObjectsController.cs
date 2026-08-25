using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UptimeHarbor.Application.ObjectAdministration;
using UptimeHarbor.Application.ObjectAdministration.Models;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Web.Controllers.Models;
using UptimeHarbor.Web.Http;

namespace UptimeHarbor.Web.Controllers;

[ApiController]
[Route("api/admin/objects")]
[Authorize(Roles = ApplicationRoles.ADMINISTRATOR)]
public class AdminObjectsController(IObjectAdministrationService administrationService) : ControllerBase
{
    [HttpGet("servers")]
    public async Task<JsonResult> GetServersAsync(CancellationToken cancellationToken)
    {
        Application.Common.ServiceResult<IReadOnlyList<AdminServerDto>> result =
            await administrationService.GetServersAsync(cancellationToken);
        return JsonResultMapper.ToJsonResult(result);
    }

    [HttpPost("servers")]
    [ValidateAntiForgeryToken]
    public async Task<JsonResult> CreateServerAsync(
        ObjectSettingsRequest request,
        CancellationToken cancellationToken)
    {
        Application.Common.ServiceResult<AdminServerDto> result =
            await administrationService.CreateServerAsync(
                request.EndpointUrl ?? string.Empty,
                request.Description,
                request.IsMonitoringEnabled,
                cancellationToken);
        return JsonResultMapper.ToJsonResult(result);
    }

    [HttpPut("servers/{serverId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<JsonResult> UpdateServerAsync(
        Guid serverId,
        ObjectSettingsRequest request,
        CancellationToken cancellationToken)
    {
        Application.Common.ServiceResult<AdminServerDto> result =
            await administrationService.UpdateServerAsync(
                serverId,
                request.EndpointUrl ?? string.Empty,
                request.Description,
                request.IsMonitoringEnabled,
                cancellationToken);
        return JsonResultMapper.ToJsonResult(result);
    }

    [HttpGet("servers/{serverId:guid}/containers")]
    public async Task<JsonResult> GetContainersAsync(
        Guid serverId,
        CancellationToken cancellationToken)
    {
        Application.Common.ServiceResult<IReadOnlyList<AdminContainerDto>> result =
            await administrationService.GetContainersAsync(serverId, cancellationToken);
        return JsonResultMapper.ToJsonResult(result);
    }

    [HttpPost("servers/{serverId:guid}/containers")]
    [ValidateAntiForgeryToken]
    public async Task<JsonResult> CreateContainerAsync(
        Guid serverId,
        ObjectSettingsRequest request,
        CancellationToken cancellationToken)
    {
        Application.Common.ServiceResult<AdminContainerDto> result =
            await administrationService.CreateContainerAsync(
                serverId,
                request.EndpointUrl ?? string.Empty,
                request.Description,
                request.IsMonitoringEnabled,
                cancellationToken);
        return JsonResultMapper.ToJsonResult(result);
    }

    [HttpPut("containers/{containerId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<JsonResult> UpdateContainerAsync(
        Guid containerId,
        ObjectSettingsRequest request,
        CancellationToken cancellationToken)
    {
        Application.Common.ServiceResult<AdminContainerDto> result =
            await administrationService.UpdateContainerAsync(
                containerId,
                request.EndpointUrl ?? string.Empty,
                request.Description,
                request.IsMonitoringEnabled,
                cancellationToken);
        return JsonResultMapper.ToJsonResult(result);
    }

    [HttpGet("servers/{serverId:guid}/deletion-impact")]
    public async Task<JsonResult> GetServerDeletionImpactAsync(
        Guid serverId,
        CancellationToken cancellationToken)
    {
        Application.Common.ServiceResult<ServerDeletionImpactDto> result =
            await administrationService.GetServerDeletionImpactAsync(serverId, cancellationToken);
        return JsonResultMapper.ToJsonResult(result);
    }

    [HttpDelete("servers/{serverId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<JsonResult> DeleteServerAsync(
        Guid serverId,
        DeleteServerRequest request,
        CancellationToken cancellationToken)
    {
        Application.Common.ServiceResult result = await administrationService.DeleteServerAsync(
            serverId,
            request.ExpectedContainerCount,
            cancellationToken);
        return JsonResultMapper.ToJsonResult(result);
    }

    [HttpDelete("containers/{containerId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<JsonResult> DeleteContainerAsync(
        Guid containerId,
        CancellationToken cancellationToken)
    {
        Application.Common.ServiceResult result =
            await administrationService.DeleteContainerAsync(containerId, cancellationToken);
        return JsonResultMapper.ToJsonResult(result);
    }
}
