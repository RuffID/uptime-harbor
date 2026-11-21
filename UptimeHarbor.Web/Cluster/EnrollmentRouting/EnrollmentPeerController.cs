using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Installation.Renewal;

namespace UptimeHarbor.Web.Cluster.EnrollmentRouting;

/// <summary>Только внутренний verified voter; cookie/anonymous browser не предоставляет эти полномочия.</summary>
[ApiController]
[AllowAnonymous]
[Route("internal/cluster/v1/enrollment")]
[RequestSizeLimit(EnrollmentPeerProtocol.MAX_BYTES)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class EnrollmentPeerController(EnrollmentPeerService service, EnrollmentRateLimiter limits,
    ILogger<EnrollmentPeerController> logger, VoterCredentialPeerService? renewal = null,
    VoterCredentialAdmission? admission = null) : ControllerBase
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

    [HttpPost("renew")]
    public async Task<IActionResult> RenewAsync(CancellationToken token)
    {
        if (renewal is null)
        {
            return NotFound();
        }

        if (HttpContext.Items[typeof(PeerCredential)] is not PeerCredential presented
            || HttpContext.Items[typeof(PeerBoundaryMiddleware.RenewalServer)] is not PeerBoundaryMiddleware.RenewalServer local)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        return await ExecuteAsync<VoterCredentialSigningRequest, EnrollmentIssuedArtifact>(
            (call, peer, cancellationToken) => renewal.IssueAsync(call, peer, presented, cancellationToken,
                local.CredentialId, local.Fingerprint), token);
    }

    [HttpPost("handoff")]
    public async Task<IActionResult> HandoffAsync(CancellationToken token)
    {
        if (renewal is null)
        {
            return NotFound();
        }

        if (HttpContext.Items[typeof(PeerCredential)] is not PeerCredential presented
            || HttpContext.Items[typeof(PeerBoundaryMiddleware.RenewalServer)] is not PeerBoundaryMiddleware.RenewalServer local)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        return await ExecuteAsync<VoterCredentialHandoff, VoterCredentialHandoff>(
            (handoff, peer, cancellationToken) => renewal.ConfirmHandoffAsync(handoff, peer, presented,
                local.CredentialId, local.Fingerprint, cancellationToken), token);
    }

    [HttpPost("rotation/nonce")]
    public Task<IActionResult> RotationNonceAsync(CancellationToken token)
    {
        return ExecuteRotationAsync<VoterCredentialNonceCall, string>(
            (receiver, call, origin, ct) => receiver.IssueNonceAsync(call, origin, ct), token);
    }

    [HttpPost("retirement-handoff")]
    public async Task<IActionResult> RetirementHandoffAsync(CancellationToken token)
    {
        if (renewal is null)
        {
            return NotFound();
        }

        if (HttpContext.Items[typeof(PeerCredential)] is not PeerCredential presented
            || HttpContext.Items[typeof(PeerBoundaryMiddleware.RenewalServer)] is not PeerBoundaryMiddleware.RenewalServer local)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        return await ExecuteAsync<VoterCredentialRetirementIntent, VoterCredentialRetirementEvidence>(
            (intent, peer, ct) => renewal.AttestRetirementAsync(intent, peer, presented, local.CredentialId, local.Fingerprint,
                HttpContext.Features.Get<DynamicPeerTls.Snapshot>(), local.LegacyCertificate, ct), token);
    }

    [HttpPost("rotation/authorize")]
    public Task<IActionResult> RotationAuthorizeAsync(CancellationToken token)
    {
        return ExecuteRotationAsync<VoterCredentialAuthorizeCall, CommandResult>(
            (receiver, call, origin, ct) => receiver.AuthorizeAsync(call, origin, ct), token);
    }

    [HttpPost("rotation/bind")]
    public Task<IActionResult> RotationBindAsync(CancellationToken token)
    {
        return ExecuteRotationAsync<VoterCredentialBindCall, CommandResult>(
            (receiver, call, origin, ct) => receiver.BindAsync(call, origin, ct), token);
    }

    [HttpPost("rotation/retire")]
    public Task<IActionResult> RotationRetireAsync(CancellationToken token)
    {
        return ExecuteRotationAsync<VoterCredentialRetirementCall, CommandResult>(
            (receiver, call, origin, ct) => receiver.RetireAsync(call, origin, ct), token);
    }

    private async Task<IActionResult> ExecuteRotationAsync<TRequest, TResponse>(
        Func<VoterCredentialAdmission, TRequest, VoterCredentialAdmissionOrigin, CancellationToken, Task<TResponse>> action,
        CancellationToken token)
    {
        if (admission is null)
        {
            return NotFound();
        }

        if (HttpContext.Items[typeof(VoterCredentialAdmissionOrigin)] is not VoterCredentialAdmissionOrigin
                { Peer: not null, Presented: not null } origin
            || !ReferenceEquals(HttpContext.Items[typeof(PeerEnvelope)], origin.Peer))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        return await ExecuteAsync<TRequest, TResponse>((call, _, ct) => action(admission, call, origin, ct), token);
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
