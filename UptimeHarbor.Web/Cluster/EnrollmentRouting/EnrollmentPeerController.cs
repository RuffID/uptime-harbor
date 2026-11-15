using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;

namespace UptimeHarbor.Web.Cluster.EnrollmentRouting;

/// <summary>Только внутренний verified voter; cookie/anonymous browser не предоставляет эти полномочия.</summary>
[ApiController]
[AllowAnonymous]
[Route("internal/cluster/v1/enrollment")]
[RequestSizeLimit(EnrollmentPeerProtocol.MAX_BYTES)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class EnrollmentPeerController(EnrollmentPeerService service, EnrollmentRateLimiter limits,
    ILogger<EnrollmentPeerController> logger) : ControllerBase
{
    [HttpPost("coordinate")]
    public async Task<IActionResult> CoordinateAsync(CancellationToken token)
    {
        return await ExecuteAsync<EnrollmentCoordinatedRequest, CommandResult>(service.CoordinateAsync, token);
    }

    [HttpPost("issue")]
    public async Task<IActionResult> IssueAsync(CancellationToken token)
    {
        return await ExecuteAsync<EnrollmentSigningRequest, EnrollmentIssuedArtifact>(service.IssueAsync, token);
    }

    [HttpPost("grant")]
    public async Task<IActionResult> GrantAsync(CancellationToken token)
    {
        return await ExecuteAsync<OnlineGrantSigningRequest, OnlineGrant>(service.GrantAsync, token);
    }

    private async Task<IActionResult> ExecuteAsync<TRequest, TResponse>(Func<TRequest, PeerEnvelope, CancellationToken, Task<TResponse>> action,
        CancellationToken token)
    {
        if (HttpContext.Items[typeof(PeerEnvelope)] is not PeerEnvelope peer)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        string source = "peer/" + peer.FromNodeId.ToString("D");
        if (!limits.TryAcquire(source))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            if (Request.ContentType != "application/json" || Request.ContentLength > EnrollmentPeerProtocol.MAX_BYTES
                || Request.Headers.ContentEncoding.Count != 0)
            {
                throw new InvalidDataException("EnrollmentPeerRequestLimitOrType");
            }

            byte[] bytes = await RevisionBatchCodec.ReadAsync(Request.Body, EnrollmentPeerProtocol.MAX_BYTES, timeout.Token);
            TRequest request = StrictOnboardingJson.Decode<TRequest>(bytes);
            TResponse response = await action(request, peer, timeout.Token);
            byte[] result = StrictOnboardingJson.Encode(response);
            if (result.Length > EnrollmentPeerProtocol.MAX_BYTES)
            {
                throw new InvalidDataException("EnrollmentPeerResponseLimit");
            }

            return File(result, "application/json");
        }
        catch (Exception error) when (error is InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or CryptographicException or FormatException or ArgumentException or HttpRequestException or OperationCanceledException)
        {
            limits.Rejected(source);
            logger.LogWarning("Enrollment peer request rejected {NodeId}.", peer.FromNodeId);
            return StatusCode(StatusCodes.Status409Conflict);
        }
    }
}
