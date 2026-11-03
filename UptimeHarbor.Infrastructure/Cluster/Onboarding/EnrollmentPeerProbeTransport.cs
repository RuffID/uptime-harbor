using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Только фиксированный peer TLS probe. DNS закрепляется на каждом connect; нет redirect/proxy/cookies.</summary>
public class EnrollmentPeerProbeTransport(IOptions<InstallationOptions> options)
{
    public const string PATH = "/internal/enrollment-peer/v1/probe";
    private HttpMessageHandler? testHandler;

    internal EnrollmentPeerProbeTransport(IOptions<InstallationOptions> options, HttpMessageHandler handler) : this(options)
    {
        testHandler = handler;
    }

    public async Task ProbeAsync(Uri peerAddress, X509Certificate2 local, RevisionImage image,
        EnrollmentPeerProbe probe, CancellationToken token)
    {
        SetupPeerAddressPolicy addresses = new(options.Value);
        addresses.ValidateUri(peerAddress.AbsoluteUri);
        OperationalNode target = image.Operations.Nodes[probe.ToNodeId];
        if (target.Address != peerAddress.AbsoluteUri || target.Removed || !local.HasPrivateKey
            || probe.Version != 1 || probe.RequestId == Guid.Empty || !EnrollmentProofs.IsChallenge(probe.Challenge))
        {
            throw new InvalidDataException("EnrollmentPeerProbeTargetInvalid");
        }

        using HttpMessageInvoker transport = new(testHandler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.Zero, MaxResponseHeadersLength = 16,
            ConnectCallback = addresses.ConnectAsync,
            SslOptions = new()
            {
                ClientCertificates = new X509CertificateCollection { local },
                RemoteCertificateValidationCallback = (_, certificate, _, errors) => certificate is X509Certificate2 leaf
                    && ValidateServer(leaf, errors, image, target.NodeId, DateTimeOffset.UtcNow)
            }
        }, disposeHandler: testHandler is null);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(peerAddress, PATH));
        request.Content = new ByteArrayContent(StrictOnboardingJson.Encode(probe));
        request.Content.Headers.ContentType = new("application/json");
        using HttpResponseMessage response = await transport.SendAsync(request, timeout.Token);
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "application/json"
            || response.Content.Headers.ContentLength > 8192 || response.Content.Headers.ContentEncoding.Count != 0)
        {
            throw new InvalidDataException("EnrollmentPeerProbeRejected");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        byte[] bytes = new byte[8193];
        int count = 0;
        while (count < bytes.Length)
        {
            int read = await stream.ReadAsync(bytes.AsMemory(count), timeout.Token);
            if (read == 0)
            {
                break;
            }

            count += read;
        }

        if (count > 8192)
        {
            throw new InvalidDataException("EnrollmentPeerProbeLimit");
        }

        EnrollmentPeerProbeReply reply = StrictOnboardingJson.Decode<EnrollmentPeerProbeReply>(bytes.AsSpan(0, count));
        if (reply.Version != 1 || reply.Request != probe || reply.ResponderNodeId != probe.ToNodeId)
        {
            throw new InvalidDataException("EnrollmentPeerProbeBindingInvalid");
        }
    }

    public static bool ValidateServer(X509Certificate2 leaf, SslPolicyErrors errors, RevisionImage image,
        Guid target, DateTimeOffset now)
    {
        // Chain errors системного store ожидаемы для application CA; имя TLS никогда не игнорируется.
        if ((errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
        {
            return false;
        }

        string digest = EnrollmentCertificates.Hash(leaf.RawData);
        return image.Operations.Credentials.Values.Any(item => item.NodeId == target && item.Fingerprint == digest)
            && EnrollmentLiveTrust.Validate(image, leaf, now);
    }

    public static string Challenge()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }
}
