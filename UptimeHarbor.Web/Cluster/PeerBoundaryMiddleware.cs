using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Web.Security;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Web.Controllers;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Web.Cluster;

public class PeerBoundaryMiddleware(RequestDelegate next, IOptions<ClusterOptions> configured,
    IOptions<InstallationOptions>? installationOptions = null, InstallationState? installation = null)
{
    public async Task InvokeAsync(HttpContext context, IPeerTrustSource trust,
        NodeReadiness readiness, TimeProvider timeProvider, ClusterPeerRuntime runtime, IClusterStateView? state = null,
        DynamicPeerTls? dynamicTls = null, RuntimeWriteGate? writes = null,
        EnrollmentPeerProbes? probes = null, EnrollmentRateLimiter? enrollmentLimits = null)
    {
        ClusterOptions options = configured.Value;
        bool peerPath = context.Request.Path.StartsWithSegments("/internal");
        bool managed = installationOptions?.Value.Setup == true;
        bool peerListener = (options.Enabled || managed) && context.Connection.LocalPort == options.PeerListenPort;
        if (context.Request.Path == EnrollmentPeerProbeTransport.PATH)
        {
            string? address = installationOptions?.Value.PeerEndpoint;
            if (!managed || !peerListener || !context.Request.IsHttps || options.TlsBoundary != "Direct"
                || address is null || context.Request.Host.Value != new Uri(address).Authority
                || readiness.Get("Clock").Status != "Ready" || probes is null || enrollmentLimits is null)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await EnrollmentPeerProbeEndpoint.HandleAsync(context, probes, enrollmentLimits);
            return;
        }

        if (EnrollmentController.IsRoute(context.Request.Path))
        {
            string origin = context.Request.Scheme + "://" + context.Request.Host.Value;
            string browser = installationOptions?.Value.BrowserOrigin ?? string.Empty;
            string suppliedOrigin = context.Request.Headers.Origin.ToString();
            if (!managed || peerListener || !context.Request.IsHttps || origin != browser
                || suppliedOrigin.Length > 0 && suppliedOrigin != browser
                || context.Request.Headers.ContainsKey("Cookie") || context.Request.Headers.ContainsKey("Authorization"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            context.Response.Headers.CacheControl = "no-store";
            await next(context);
            return;
        }

        if (!peerPath && !peerListener)
        {
            if (managed && SetupBoundaryMiddleware.Allowed(context.Request.Path))
            {
                await next(context);
                return;
            }

            if (managed && installation?.CommonOpen != true)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            if (options.Enabled && (state is null || !readiness.CanWork(state)))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            await next(context);
            return;
        }

        if (managed && installation?.Current?.Phase is not (UptimeHarbor.Application.Installation.InstallationPhase.ClusterQuarantine
            or UptimeHarbor.Application.Installation.InstallationPhase.CatchingUp
            or UptimeHarbor.Application.Installation.InstallationPhase.AwaitingActivation
            or UptimeHarbor.Application.Installation.InstallationPhase.ClusterReady))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (!peerListener || !peerPath || context.Request.Host.Host != new Uri(options.PublicUri).Host)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        long generation = installation?.Current?.RuntimeGeneration ?? 0;
        IDisposable? operation = null;
        RuntimeGenerationContext? generationContext = null;
        if (managed)
        {
            RuntimeWriteGate gate = writes ?? throw new InvalidOperationException("RuntimeGateMissing");
            operation = gate.Enter(generation);
            generationContext = new(() => gate.Verify(generation));
        }

        using IDisposable? requestOperation = operation;
        using RuntimeGenerationContext? requestGeneration = generationContext;

        if (readiness.Get("Clock").Status != "Ready")
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        bool native = context.Request.Path.StartsWithSegments("/internal/raft");
        bool health = context.Request.Path == "/internal/cluster/v1/health";
        bool commands = context.Request.Path == "/internal/cluster/v1/commands";
        if (!native && !health && !commands)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        long limit = options.HealthMaxBytes;
        if (commands)
        {
            limit = options.BatchMaxBytes;
        }

        if (native)
        {
            limit = options.NativeMaxBytes;
        }

        if (context.Request.ContentLength > limit)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        IHttpMaxRequestBodySizeFeature? bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is not null && !bodyLimit.IsReadOnly)
        {
            bodyLimit.MaxRequestBodySize = limit;
        }
        else if (context.Request.ContentLength != 0 && !HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        PeerEnvelope? envelope = PeerHeaders.Decode(context.Request.Headers[PeerHeaders.ENVELOPE].ToString());
        if (envelope is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        PeerProtocolGuard guard = new(options, trust, timeProvider);
        PeerCredential? credential = await guard.ValidateAsync(envelope, context.Request.ContentLength ?? 0,
            limit, context.RequestAborted);
        if (credential is null || (native && !credential.IsVoting && !credential.IsAdmission))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (native && !NativeRaftIdentity.Validate(credential, context.Request.Headers["X-Raft-Node-ID"].ToString(),
            context.Request.Headers["X-Raft-Message-Type"].ToString(), context.Request.Headers["X-Raft-Term"].ToString(),
            context.Request.Headers["X-Raft-State-Version"].ToString(), envelope))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using X509Certificate2? certificate = await GetCertificateAsync(context, options);
        if (certificate is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        bool validCertificate;
        if (managed)
        {
            validCertificate = dynamicTls?.ValidateRequest(certificate, options, credential, timeProvider.GetUtcNow()) == true;
        }
        else
        {
            using X509Certificate2 root = X509CertificateLoader.LoadCertificateFromFile(options.ClientCaPath);
            validCertificate = new PeerCertificateValidator().Validate(certificate, root, options, credential, timeProvider.GetUtcNow());
        }

        if (!validCertificate)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        PeerEnvelope reply = new(options.ClusterId, options.Generation, options.NodeId,
            envelope.FromNodeId, options.StorageGeneration, options.CredentialId, envelope.RequestId,
            timeProvider.GetUtcNow(), options.Compatibility, envelope.Challenge);
        context.Response.Headers[PeerHeaders.ENVELOPE] = PeerHeaders.Encode(reply);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Remove("Set-Cookie");

        if (native)
        {
            await runtime.ProcessRaftAsync(context, envelope);
            return;
        }

        if (!runtime.AcceptNonce(envelope))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        if (commands)
        {
            if (!credential.IsVoting || !runtime.CanAcceptCommands)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            context.Items[typeof(PeerEnvelope)] = envelope;
            await next(context);
            return;
        }

        await runtime.WriteHealthAsync(context, envelope);
    }

    private static async Task<X509Certificate2?> GetCertificateAsync(HttpContext context, ClusterOptions options)
    {
        if (options.TlsBoundary == "Direct")
        {
            if (!context.Request.IsHttps)
            {
                return null;
            }

            X509Certificate2? certificate = await context.Connection.GetClientCertificateAsync(context.RequestAborted);
            if (certificate is null)
            {
                return null;
            }

            return new X509Certificate2(certificate);
        }

        IPAddress? remote = context.Connection.RemoteIpAddress;
        bool trustedProxy = remote is not null && options.TrustedProxyAddresses
            .Any(address => IPAddress.Parse(address).Equals(remote));
        if (!trustedProxy || context.Request.Headers["Ssl-Client-Verify"] != "SUCCESS")
        {
            return null;
        }

        string encoded = context.Request.Headers["Ssl-Client-Cert"].ToString();
        if (encoded.Length > 49152)
        {
            return null;
        }

        string pem = Uri.UnescapeDataString(encoded);
        try
        {
            return X509Certificate2.CreateFromPem(pem);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}
