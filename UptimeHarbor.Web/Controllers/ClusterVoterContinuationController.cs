using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Web.Controllers.Models;
using UptimeHarbor.Web.Http;

namespace UptimeHarbor.Web.Controllers;

[ApiController]
[Route("api/admin/cluster/credential-continuation/{requestId:guid}")]
[Authorize(Roles = ApplicationRoles.ADMINISTRATOR)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class ClusterVoterContinuationController(
    IOptions<InstallationOptions> options, ILogger<ClusterVoterContinuationController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<JsonResult> ReadAsync(Guid requestId, CancellationToken token)
    {
        if (ResolveAdministrator() is not { } access)
        {
            return Forbidden();
        }

        using CancellationTokenSource deadline = CreateDeadline(token);
        try
        {
            VoterContinuationAdministratorStatus status =
                await access.Authority.ReadAdministratorStatusAsync(requestId, deadline.Token);
            return JsonResultMapper.ToJsonResult(ServiceResult<VoterContinuationAdministratorStatus>.Ok(status));
        }
        catch (Exception error) when (Unavailable(error, token))
        {
            return Unconfirmed("Запрос недоступен. Проверьте ID операции и откройте панель на действующем лидере кластера.");
        }
    }

    [HttpPost]
    [RequestSizeLimit(8192)]
    [EnableRateLimiting("onboarding-administrator")]
    [ValidateAntiForgeryToken]
    public async Task<JsonResult> ConfirmAsync(Guid requestId, ConfirmVoterContinuationRequest request, CancellationToken token)
    {
        if (ResolveAdministrator() is not { } access)
        {
            return Forbidden();
        }

        using CancellationTokenSource deadline = CreateDeadline(token);
        try
        {
            CommandResult result = await access.Authority.AuthorizeAsync(requestId, request.RequestDigest, request.Password, deadline.Token);
            if (result.Outcome != OperationOutcome.Committed)
            {
                logger.LogWarning("Credential continuation confirmation rejected for request {RequestId} by actor {ActorId}", requestId, access.Actor);
                return Unconfirmed("Подтверждение отклонено. Проверьте текущее состояние запроса.");
            }

            logger.LogInformation("Accepted credential continuation confirmation {ConfirmationId} returned for request {RequestId} to actor {ActorId}",
                result.OperationId, requestId, access.Actor);
            return JsonResultMapper.ToJsonResult(ServiceResult<Guid>.Ok(result.OperationId));
        }
        catch (UnauthorizedAccessException error) when (error.Message == "VoterContinuationReauthenticationFailed")
        {
            logger.LogWarning("Credential continuation reauthentication refused for request {RequestId} by actor {ActorId}", requestId, access.Actor);
            return JsonResultMapper.ToJsonResult(ServiceResult.Fail(ServiceErrorType.Validation,
                "Повторная проверка пароля не пройдена. Проверьте пароль и доступ к учётной записи."));
        }
        catch (Exception error) when (Unavailable(error, token))
        {
            logger.LogWarning("Credential continuation confirmation unconfirmed for request {RequestId} by actor {ActorId}", requestId, access.Actor);
            return Unconfirmed("Подтверждение не удалось проверить. Откройте тот же запрос на действующем лидере и проверьте результат.");
        }
    }

    private (VoterContinuationAuthority Authority, Guid Actor)? ResolveAdministrator()
    {
        string configured = options.Value.BrowserOrigin;
        string actual = Request.Scheme + "://" + Request.Host.Value;
        string supplied = Request.Headers.Origin.ToString();
        if (!Request.IsHttps || actual != configured
            || (HttpMethods.IsPost(Request.Method) || Request.Headers.ContainsKey("Origin")) && supplied != configured)
        {
            return null;
        }

        IServiceProvider services = HttpContext.RequestServices;
        if (services.GetService<IVoterContinuationAdministrator>()?.CommonActorId is not Guid actor
            || services.GetService<VoterContinuationAuthority>() is not { } authority)
        {
            return null;
        }

        return (authority, actor);
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken token)
    {
        CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        return deadline;
    }

    private static bool Unavailable(Exception error, CancellationToken token)
    {
        return error is UnauthorizedAccessException
            || error is OperationCanceledException && !token.IsCancellationRequested
            || error is InvalidOperationException && error.Message is "EnrollmentReadMajorityUnavailable"
                or "EnrollmentReadMajorityChanged" or "DecisionUnavailable" or "StaleRuntimeGeneration"
                or "Raft recovery is unconfirmed." or "Cluster writer is not ready.";
    }

    private static JsonResult Forbidden()
    {
        return JsonResultMapper.ToJsonResult(ServiceResult.Fail(ServiceErrorType.Forbidden, "Требуется общий доступ администратора на доверенном адресе."));
    }

    private static JsonResult Unconfirmed(string message)
    {
        return JsonResultMapper.ToJsonResult(ServiceResult.Fail(ServiceErrorType.Conflict, message));
    }
}
