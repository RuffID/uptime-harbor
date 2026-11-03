using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Web.Controllers.Models;
using UptimeHarbor.Web.Http;
using UptimeHarbor.Web.Security;

namespace UptimeHarbor.Web.Controllers;

[ApiController]
[Route("api/setup")]
[Authorize(AuthenticationSchemes = SetupAuthentication.SCHEME)]
[EnableRateLimiting(WebSecurityPolicies.LOGIN_RATE_LIMIT_POLICY)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(4096)]
public class SetupController(SetupAccountLogin login, InstallationPreparation preparation,
    InstallationLifecycle lifecycle, InstallationState state, IAntiforgery antiforgery,
    SetupAuthorityCoordinator authority) : ControllerBase
{
    [HttpGet("session")]
    [AllowAnonymous]
    public async Task<IActionResult> SessionAsync()
    {
        AuthenticateResult existing = await HttpContext.AuthenticateAsync(SetupAuthentication.SCHEME);
        if (existing.Succeeded)
        {
            HttpContext.User = existing.Principal!;
        }

        AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return JsonResultMapper.ToJsonResult(ServiceResult<object>.Ok(new
        {
            authenticated = existing.Succeeded, token = tokens.RequestToken
        }));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LoginAsync(SetupLoginRequest request, CancellationToken token)
    {
        ServiceResult<SetupSession> result = await login.LoginAsync(request.Login, request.Password, token);
        if (!result.Success)
        {
            return JsonResultMapper.ToJsonResult(ServiceResult.Fail(result.Error!));
        }

        await HttpContext.SignInAsync(SetupAuthentication.SCHEME, SetupAuthentication.Principal(result.Data!),
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });
        return JsonResultMapper.ToJsonResult(ServiceResult.Ok());
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        LocalInstallationProfile profile = state.Current ?? throw new InvalidOperationException("InstallationProfileMissing");
        return JsonResultMapper.ToJsonResult(ServiceResult<SetupStatus>.Ok(InstallationPreparation.Status(profile, state.CommonOpen)));
    }

    [HttpPost("prepare")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PrepareAsync(SetupPrepareRequest request, CancellationToken token)
    {
        SetupSession? session = SetupAuthentication.Read(User);
        if (session is null)
        {
            return Unauthorized();
        }

        ServiceResult<SetupStatus> result = await preparation.PrepareAsync(session,
            new(request.Mode, request.ExpectedRevision, request.ExpectedGeneration,
                request.CommonLogin, request.CommonPassword), token);
        if (result.Success && result.Data!.Phase == nameof(InstallationPhase.Preparing))
        {
            result = await lifecycle.AdvanceAsync(result.Data.Revision, result.Data.Generation,
                InstallationPhase.Quiescing, null, token);
        }

        if (result.Success && result.Data!.Phase == nameof(InstallationPhase.Quiescing))
        {
            result = await lifecycle.AdvanceAsync(result.Data.Revision, result.Data.Generation,
                InstallationPhase.LocalPrepared, null, token);
        }

        if (result.Success && result.Data!.Phase == nameof(InstallationPhase.LocalPrepared) && result.Data.Mode == "Bootstrap")
        {
            result = await authority.PrepareBootstrapAsync(token);
        }

        return JsonResultMapper.ToJsonResult(result);
    }

    [HttpPost("logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LogoutAsync(CancellationToken token)
    {
        SetupSession? session = SetupAuthentication.Read(User);
        if (session is null || !await login.ValidateAsync(session, token))
        {
            return JsonResultMapper.ToJsonResult(ServiceResult.Fail(ServiceErrorType.Unauthorized, "Требуется локальный вход."));
        }

        await HttpContext.SignOutAsync(SetupAuthentication.SCHEME);
        return JsonResultMapper.ToJsonResult(ServiceResult.Ok());
    }
}
