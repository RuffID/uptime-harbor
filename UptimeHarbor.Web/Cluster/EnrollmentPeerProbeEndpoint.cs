using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Web.Cluster;

/// <summary>Фиксированный read-only handler до runtime; доступен только на Direct peer listener с mTLS.</summary>
public static class EnrollmentPeerProbeEndpoint
{
    public static async Task HandleAsync(HttpContext context, EnrollmentPeerProbes probes, EnrollmentRateLimiter limits)
    {
        string source = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!limits.TryAcquire(source))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            if (!HttpMethods.IsPost(context.Request.Method) || context.Request.ContentType != "application/json"
                || context.Request.ContentLength > 8192 || context.Request.Headers.ContainsKey("Cookie")
                || context.Request.Headers.ContainsKey("Authorization") || context.Request.Headers.ContainsKey("Origin"))
            {
                throw new InvalidDataException("EnrollmentPeerProbeRequestInvalid");
            }

            X509Certificate2 certificate = await context.Connection.GetClientCertificateAsync(timeout.Token)
                ?? throw new UnauthorizedAccessException("EnrollmentPeerProbeCertificateRequired");
            byte[] bytes = new byte[8193];
            int count = 0;
            while (count < bytes.Length)
            {
                int read = await context.Request.Body.ReadAsync(bytes.AsMemory(count), timeout.Token);
                if (read == 0)
                {
                    break;
                }

                count += read;
            }

            if (count > 8192)
            {
                throw new InvalidDataException("EnrollmentPeerProbeRequestLimit");
            }

            EnrollmentPeerProbe request = StrictOnboardingJson.Decode<EnrollmentPeerProbe>(bytes.AsSpan(0, count));
            EnrollmentPeerProbeReply reply = probes.Reply(request, certificate);
            context.Response.ContentType = "application/json";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.Body.WriteAsync(StrictOnboardingJson.Encode(reply), timeout.Token);
        }
        catch (Exception error) when (error is InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or FormatException or CryptographicException or KeyNotFoundException or OperationCanceledException)
        {
            limits.Rejected(source);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
        }
    }
}
