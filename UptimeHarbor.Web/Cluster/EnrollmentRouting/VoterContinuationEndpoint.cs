using System.Security.Cryptography;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation;
using static UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal.VoterContinuationWire;

namespace UptimeHarbor.Web.Cluster.EnrollmentRouting;

/// <summary>Отдельный HTTPS ingress для известного voter с истёкшим managed leaf.</summary>
internal static class VoterContinuationEndpoint
{
    internal const string ROOT = VoterContinuationWire.ROOT;
    private const int MAX_BYTES = 65536;

    internal static bool Owns(PathString path)
    {
        return path.StartsWithSegments("/api/credential-continuation");
    }

    internal static async Task EnterAsync(HttpContext context, RequestDelegate next, bool peerListener, InstallationOptions? options,
        InstallationState? installation, RuntimeWriteGate? writes)
    {
        HttpRequest request = context.Request;
        context.Response.Headers.CacheControl = "no-store";
        if (peerListener || options?.Setup != true || installation?.CommonOpen != true
            || installation.Current is not { Phase: InstallationPhase.ClusterReady } profile || writes is null
            || !request.IsHttps || "https://" + request.Host.Value != options.BrowserOrigin
            || !HttpMethods.IsPost(request.Method) || request.ContentType != "application/json"
            || request.PathBase.HasValue || request.QueryString.HasValue
            || request.Headers.ContainsKey("Cookie") || request.Headers.ContainsKey("Authorization")
            || request.Headers.ContainsKey("Origin") || request.Headers.ContainsKey(PeerHeaders.ENVELOPE))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        string? path = request.Path.Value;
        if (path is not (ROOT + "/nonce" or ROOT + "/request" or ROOT + "/status" or ROOT + "/register" or ROOT + "/issue"
            or ROOT + "/bind" or ROOT + "/" + VoterContinuationAuthority.SLICE))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (request.ContentLength > MAX_BYTES)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        string source = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        context.Features.Set(new Ingress(profile.RuntimeGeneration, writes, source));
        await next(context);
    }

    internal static async Task HandleAsync(HttpContext context)
    {
        Ingress? ingress = context.Features.Get<Ingress>();
        if (ingress is null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        IServiceProvider services = context.RequestServices;
        EnrollmentRateLimiter? limits = services.GetKeyedService<EnrollmentRateLimiter>(VoterContinuationAuthority.NONCE_KEY);
        if (limits is null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        string source = ingress.Source;
        if (!limits.TryAcquire(source))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        CancellationToken token = timeout.Token;
        try
        {
            using IDisposable operation = ingress.Writes.Enter(ingress.Generation);
            using RuntimeGenerationContext generation = new(() => ingress.Writes.Verify(ingress.Generation));

            byte[] bytes = await ReadBodyAsync(context.Request, token);
            byte[] response = context.Request.Path.Value switch
            {
                ROOT + "/nonce" => await IssueNonceAsync(StrictOnboardingJson.Decode<Challenge>(bytes), services, token),
                ROOT + "/request" => await RequestAsync(StrictOnboardingJson.Decode<VoterCredentialProofRequest>(bytes), services, token),
                ROOT + "/status" => await StatusAsync(StrictOnboardingJson.Decode<VoterCredentialProofRequest>(bytes), services, token),
                ROOT + "/register" => await RegisterAsync(StrictOnboardingJson.Decode<Registration>(bytes), services, token),
                ROOT + "/issue" => await IssueLeafAsync(StrictOnboardingJson.Decode<Issuance>(bytes), services, token),
                ROOT + "/bind" => await BindAsync(StrictOnboardingJson.Decode<Binding>(bytes), services, token),
                ROOT + "/" + VoterContinuationAuthority.SLICE => await services.GetRequiredService<VoterContinuationAuthority>()
                    .ReadAcceptedSliceAsync(StrictOnboardingJson.Decode<SliceQuery>(bytes), token),
                _ => throw new InvalidOperationException("VoterContinuationRouteInvalid")
            };

            RuntimeGenerationContext.Verify();
            token.ThrowIfCancellationRequested();
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(response, token);
        }
        catch (Exception error) when (!context.Response.HasStarted && error is (IOException or InvalidDataException
            or UnauthorizedAccessException or InvalidOperationException or ArgumentException or FormatException
            or CryptographicException or OperationCanceledException))
        {
            limits.Rejected(source);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken token)
    {
        byte[] bytes = new byte[MAX_BYTES + 1];
        int count = await request.Body.ReadAtLeastAsync(bytes, bytes.Length, false, token);
        if (count > MAX_BYTES)
        {
            throw new InvalidDataException("VoterContinuationBodyLimit");
        }

        return bytes[..count];
    }

    private static async Task<byte[]> IssueNonceAsync(Challenge challenge, IServiceProvider services, CancellationToken token)
    {
        string? decisionDigest = challenge.AuthorizationDigest;
        bool registered = challenge.Purpose is VoterContinuationAuthority.ISSUE or VoterContinuationAuthority.BIND;
        if (registered && decisionDigest is null)
        {
            decisionDigest = challenge.RegistrationDigest;
        }
        else if (challenge.RegistrationDigest is not null || registered)
        {
            throw new InvalidDataException("VoterContinuationChallengeBindingInvalid");
        }

        VoterContinuationAuthority authority = services.GetRequiredService<VoterContinuationAuthority>();
        string nonce = await authority.IssueChallengeAsync(challenge.Call, challenge.Purpose, decisionDigest, token, challenge.CertificateDigest);
        return StrictOnboardingJson.Encode(new Nonce(nonce));
    }

    private static async Task<byte[]> RequestAsync(VoterCredentialProofRequest call, IServiceProvider services, CancellationToken token)
    {
        VoterContinuationAuthority authority = services.GetRequiredService<VoterContinuationAuthority>();
        return StrictOnboardingJson.Encode(await authority.RequestAsync(call, token));
    }

    private static async Task<byte[]> StatusAsync(VoterCredentialProofRequest call, IServiceProvider services, CancellationToken token)
    {
        VoterContinuationAuthority authority = services.GetRequiredService<VoterContinuationAuthority>();
        return StrictOnboardingJson.Encode(await authority.ReadStatusAsync(call, token));
    }

    private static async Task<byte[]> RegisterAsync(Registration registration, IServiceProvider services, CancellationToken token)
    {
        VoterContinuationAuthority authority = services.GetRequiredService<VoterContinuationAuthority>();
        return StrictOnboardingJson.Encode(await authority.RegisterAsync(registration.Call, registration.AuthorizationDigest, token));
    }

    private static async Task<byte[]> IssueLeafAsync(Issuance request, IServiceProvider services, CancellationToken token)
    {
        VoterContinuationIssuance issuance = services.GetRequiredService<VoterContinuationIssuance>();
        EnrollmentIssuedArtifact result = await issuance.IssueAsync(request.Call, request.RegistrationDigest, token);
        return StrictOnboardingJson.Encode(new IssuedLeaf(result.AttemptId, result.IssueId, result.CertificateDer, result.CertificateDigest));
    }

    private static async Task<byte[]> BindAsync(Binding request, IServiceProvider services, CancellationToken token)
    {
        VoterContinuationBind binding = services.GetRequiredService<VoterContinuationBind>();
        CommandResult decision = await binding.BindAsync(request.Call, request.RegistrationDigest, request.CertificateDigest, token);
        if (decision.Outcome != OperationOutcome.Committed)
        {
            throw new UnauthorizedAccessException("VoterContinuationBindRejected");
        }

        VoterCredentialRequest intent = request.Call.Request;
        return StrictOnboardingJson.Encode(new BoundLeaf(intent.OperationId, intent.Renewal.Issuance.IssueId, decision, decision.Index));
    }

    private sealed record Ingress(long Generation, RuntimeWriteGate Writes, string Source);
}
