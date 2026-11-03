using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Browser TLS остаётся системным. Этот transport не принимает peer CA как browser trust.</summary>
public sealed class EnrollmentTransport : IDisposable
{
    public const int MAX_BYTES = 65536;
    private readonly HttpMessageInvoker transport;
    private readonly EnrollmentEndpoints endpoints;

    public EnrollmentTransport(IOptions<InstallationOptions> options)
    {
        SetupPeerAddressPolicy policy = new(options.Value);
        endpoints = new(policy);
        transport = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            MaxResponseHeadersLength = 16,
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectCallback = policy.ConnectAsync
        });
    }

    // Изолированные тесты не создают sockets/listeners и не заменяют production TLS validation.
    internal EnrollmentTransport(HttpMessageHandler handler, EnrollmentEndpoints endpoints)
    {
        transport = new(handler);
        this.endpoints = endpoints;
    }

    public async Task<TResponse> DiscoverAsync<TResponse>(Uri origin, CancellationToken token)
    {
        return await SendAsync<TResponse>(origin, EnrollmentEndpoint.Discovery, null, token);
    }

    public async Task<TResponse> PostAsync<TRequest, TResponse>(Uri origin, EnrollmentEndpoint endpoint,
        TRequest request, CancellationToken token)
    {
        if (endpoint == EnrollmentEndpoint.Discovery)
        {
            throw new InvalidDataException("EnrollmentMethodInvalid");
        }

        byte[] body = StrictOnboardingJson.Encode(request);
        try
        {
            if (body.Length > MAX_BYTES)
            {
                throw new InvalidDataException("EnrollmentRequestLimit");
            }

            return await SendAsync<TResponse>(origin, endpoint, body, token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    private async Task<TResponse> SendAsync<TResponse>(Uri origin, EnrollmentEndpoint endpoint,
        byte[]? body, CancellationToken token)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using HttpRequestMessage request = new(HttpMethod.Get, endpoints.Resolve(origin, endpoint));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        if (body is not null)
        {
            request.Method = HttpMethod.Post;
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new("application/json");
        }

        using HttpResponseMessage response = await transport.SendAsync(request, timeout.Token);
        int status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            throw new InvalidDataException("EnrollmentRedirectRejected");
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            // Ни тело отказа, ни URI/secret не попадают в exception или лог.
            throw new HttpRequestException("EnrollmentExchangeRejected", null, response.StatusCode);
        }

        if (response.Content.Headers.ContentType?.MediaType != "application/json"
            || response.Content.Headers.ContentEncoding.Count != 0
            || response.Content.Headers.ContentLength > MAX_BYTES)
        {
            throw new InvalidDataException("EnrollmentResponseLimitOrType");
        }

        await using Stream source = await response.Content.ReadAsStreamAsync(timeout.Token);
        using MemoryStream payload = new();
        byte[] buffer = new byte[4096];
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                {
                    break;
                }

                if (payload.Length + read > MAX_BYTES)
                {
                    throw new InvalidDataException("EnrollmentResponseLimitOrType");
                }

                payload.Write(buffer, 0, read);
            }

            return StrictOnboardingJson.Decode<TResponse>(payload.GetBuffer().AsSpan(0, checked((int)payload.Length)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            CryptographicOperations.ZeroMemory(payload.GetBuffer());
        }
    }

    public void Dispose()
    {
        transport.Dispose();
    }
}
