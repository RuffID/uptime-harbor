using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Web.Http;

namespace UptimeHarbor.Web.Controllers;

/// <summary>Отдельный browser HTTPS протокол. Не принимает произвольные revision/control команды.</summary>
[ApiController]
[AllowAnonymous]
[Route("internal/enrollment/v1")]
[RequestSizeLimit(EnrollmentTransport.MAX_BYTES)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class EnrollmentController(EnrollmentGateway gateway, EnrollmentDescriptors descriptors,
    EnrollmentNonces nonces, EnrollmentRateLimiter limits, ILogger<EnrollmentController> logger,
    EnrollmentPeerProbes? probes = null) : ControllerBase
{
    public static bool IsRoute(PathString path)
    {
        return path.Value is "/internal/enrollment/v1/discovery" or "/internal/enrollment/v1/nonce"
            or "/internal/enrollment/v1/reserve" or "/internal/enrollment/v1/issue"
            or "/internal/enrollment/v1/admit" or "/internal/enrollment/v1/resume" or "/internal/enrollment/v1/renew"
            or "/internal/enrollment/v1/state" or "/internal/enrollment/v1/probe";
    }

    [HttpGet("discovery")]
    public Task<IActionResult> DiscoveryAsync(CancellationToken token)
    {
        return ExecuteAsync(_ => Task.FromResult<object>(descriptors.Discover()), token);
    }

    [HttpPost("nonce")]
    public Task<IActionResult> NonceAsync(CancellationToken token)
    {
        return ExecuteAsync(async cancellation =>
        {
            EnrollmentNonceRequest request = await ReadAsync<EnrollmentNonceRequest>(cancellation);
            if (request.Version != 1)
            {
                throw new InvalidDataException("EnrollmentVersionUnsupported");
            }

            string nonce = nonces.Issue(request.AttemptId, request.BootId, request.BootChallenge,
                request.ProcessEpoch, request.Action, request.RequestDigest);
            return nonce;
        }, token);
    }

    [HttpPost("reserve")]
    [HttpPost("issue")]
    [HttpPost("admit")]
    [HttpPost("resume")]
    [HttpPost("renew")]
    public Task<IActionResult> ExchangeAsync(CancellationToken token)
    {
        return ExecuteAsync(async cancellation =>
        {
            EnrollmentExchange exchange = await ReadAsync<EnrollmentExchange>(cancellation);
            bool action = Request.Path.Value switch
            {
                "/internal/enrollment/v1/reserve" => exchange.Proof.Action == "ReserveInvitation",
                "/internal/enrollment/v1/issue" => exchange.Proof.Action is "AuthorizeEnrollmentCertificate" or "BindEnrollmentCredential",
                "/internal/enrollment/v1/admit" => exchange.Proof.Action == "AdmitInvitedNode",
                "/internal/enrollment/v1/resume" => exchange.Proof.Action == "ResumeOnboardingAttempt",
                "/internal/enrollment/v1/renew" => exchange.Proof.Action is "RenewOnboardingAdmission" or "RenewOnboardingProcess",
                _ => false
            };
            if (!action)
            {
                throw new InvalidDataException("EnrollmentActionPurposeInvalid");
            }

            return await gateway.ExchangeAsync(exchange, cancellation);
        }, token);
    }

    [HttpPost("state")]
    public Task<IActionResult> StateAsync(CancellationToken token)
    {
        return ExecuteAsync(async cancellation =>
        {
            EnrollmentExchange exchange = await ReadAsync<EnrollmentExchange>(cancellation);
            return await gateway.StateAsync(exchange, cancellation);
        }, token);
    }

    [HttpPost("probe")]
    public Task<IActionResult> ProbeAsync(CancellationToken token)
    {
        return ExecuteAsync(async cancellation =>
        {
            EnrollmentExchange exchange = await ReadAsync<EnrollmentExchange>(cancellation);
            EnrollmentDescriptor descriptor = await gateway.ConfirmProbeAsync(exchange, cancellation);
            if (probes is null)
            {
                throw new InvalidOperationException("EnrollmentPeerProbesUnavailable");
            }

            await probes.ReverseAsync(descriptor, cancellation);
            return descriptor;
        }, token);
    }

    private async Task<IActionResult> ExecuteAsync(Func<CancellationToken, Task<object>> action, CancellationToken token)
    {
        string source = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!limits.TryAcquire(source))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            object result = await action(timeout.Token);
            byte[] bytes = StrictOnboardingJson.Encode(result);
            if (bytes.Length > EnrollmentTransport.MAX_BYTES)
            {
                throw new InvalidDataException("EnrollmentDescriptorLimit");
            }

            return File(bytes, "application/json");
        }
        catch (Exception error) when (error is InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or FormatException or ArgumentException or CryptographicException
            or HttpRequestException or OperationCanceledException)
        {
            limits.Rejected(source);
            logger.LogWarning("{EventName} {SystemSource}", "EnrollmentExchangeRejected", "EnrollmentHttps");
            return JsonResultMapper.ToJsonResult(ServiceResult.Fail(ServiceErrorType.Conflict, "EnrollmentUnavailableOrRejected"));
        }
    }

    private async Task<T> ReadAsync<T>(CancellationToken token)
    {
        if (Request.ContentType != "application/json" || Request.ContentLength > EnrollmentTransport.MAX_BYTES)
        {
            throw new InvalidDataException("EnrollmentRequestLimitOrType");
        }

        using MemoryStream payload = new();
        byte[] buffer = new byte[4096];
        try
        {
            while (true)
            {
                int read = await Request.Body.ReadAsync(buffer, token);
                if (read == 0)
                {
                    break;
                }

                if (payload.Length + read > EnrollmentTransport.MAX_BYTES)
                {
                    throw new InvalidDataException("EnrollmentRequestLimitOrType");
                }

                payload.Write(buffer, 0, read);
            }

            return StrictOnboardingJson.Decode<T>(payload.GetBuffer().AsSpan(0, checked((int)payload.Length)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            CryptographicOperations.ZeroMemory(payload.GetBuffer());
        }
    }
}
