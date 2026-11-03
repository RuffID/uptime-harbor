using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Web.Controllers.Models;
using UptimeHarbor.Web.Http;
using Microsoft.AspNetCore.RateLimiting;

namespace UptimeHarbor.Web.Controllers;

[ApiController]
[Route("api/admin/cluster/invitations")]
[Authorize(Roles = ApplicationRoles.ADMINISTRATOR)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(4096)]
public class ClusterInvitationsController(InvitationAdministration administration) : ControllerBase
{
    [HttpGet("{invitationId:guid}/operations/{operationId:guid}")]
    public async Task<JsonResult> ReadAsync(Guid invitationId, Guid operationId, CancellationToken token)
    {
        return Map(await administration.ReadAsync(invitationId, operationId, token));
    }

    [HttpPost]
    [EnableRateLimiting("onboarding-administrator")]
    [ValidateAntiForgeryToken]
    public async Task<JsonResult> IssueAsync(IssueInvitationRequest request, CancellationToken token)
    {
        return Map(await administration.IssueAsync(request.OperationId, request.ReplacesInvitationId, request.Password, token));
    }

    [HttpPost("{invitationId:guid}/revoke")]
    [EnableRateLimiting("onboarding-administrator")]
    [ValidateAntiForgeryToken]
    public async Task<JsonResult> RevokeAsync(Guid invitationId, InvitationActionRequest request, CancellationToken token)
    {
        return Map(await administration.MutateAsync("RevokeInvitation", invitationId, Guid.Empty, request.OperationId, request.Password, token));
    }

    [HttpPost("{invitationId:guid}/attempts/{attemptId:guid}/continue")]
    [EnableRateLimiting("onboarding-administrator")]
    [ValidateAntiForgeryToken]
    public async Task<JsonResult> ContinueAsync(Guid invitationId, Guid attemptId, InvitationActionRequest request, CancellationToken token)
    {
        return Map(await administration.MutateAsync("ContinueOnboardingAttempt", invitationId, attemptId, request.OperationId, request.Password, token));
    }

    [HttpPost("{invitationId:guid}/attempts/{attemptId:guid}/cancel")]
    [EnableRateLimiting("onboarding-administrator")]
    [ValidateAntiForgeryToken]
    public async Task<JsonResult> CancelAsync(Guid invitationId, Guid attemptId, InvitationActionRequest request, CancellationToken token)
    {
        return Map(await administration.MutateAsync("CancelOnboardingAttempt", invitationId, attemptId, request.OperationId, request.Password, token));
    }

    private static JsonResult Map(ServiceResult<InvitationResponse> result)
    {
        JsonResult response = JsonResultMapper.ToJsonResult(result);
        if (result.Success && result.Data!.OperationStatus == "LocalPending")
        {
            response.StatusCode = StatusCodes.Status202Accepted;
        }

        return response;
    }
}
