using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Monitoring.Models;
using UptimeHarbor.Domain;
using UptimeHarbor.Infrastructure.Monitoring;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Monitoring;

public class HttpMonitoringEndpointCheckerTests
{
    private static readonly DateTimeOffset NOW_UTC =
        new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ServerCheck_WhenResponseIsValid_ReturnsNameAndUsesAnonymousGet()
    {
        HttpRequestMessage? capturedRequest = null;
        HttpMonitoringEndpointChecker checker = CreateChecker((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(JsonResponse("{\"name\":\" server-01 \"}"));
        });

        ServerEndpointCheckResult result = await checker.CheckAsync(
            "https://server.example/health",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("server-01", result.Name);
        Assert.Equal(NOW_UTC, result.CheckedAtUtc);
        Assert.Equal(HttpMethod.Get, capturedRequest!.Method);
        Assert.Null(capturedRequest.Headers.Authorization);
    }

    [Fact]
    public async Task ContainerCheck_WhenResponseIsValid_ReturnsNameAndStartedAtUtc()
    {
        HttpMonitoringEndpointChecker checker = CreateChecker((_, _) => Task.FromResult(
            JsonResponse("{\"name\":\"Orders API\",\"startedAtUtc\":\"2026-08-25T08:00:00Z\"}")));

        ContainerEndpointCheckResult result = await ((IContainerEndpointChecker)checker).CheckAsync(
            "https://container.example/health",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("Orders API", result.Name);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 25, 8, 0, 0, TimeSpan.Zero),
            result.StartedAtUtc);
    }

    [Fact]
    public async Task Check_WhenTimeoutExpires_ReturnsTimeoutFailure()
    {
        HttpMonitoringEndpointChecker checker = CreateChecker(
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Недостижимый код.");
            },
            timeoutSeconds: 1,
            TimeProvider.System);

        ServerEndpointCheckResult result = await checker.CheckAsync(
            "https://server.example/health",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(EndpointCheckFailureKind.Timeout, result.FailureKind);
    }

    [Fact]
    public async Task Check_WhenNetworkFails_ReturnsNetworkFailure()
    {
        HttpMonitoringEndpointChecker checker = CreateChecker((_, _) =>
            throw new HttpRequestException("Network unavailable."));

        ServerEndpointCheckResult result = await checker.CheckAsync(
            "https://server.example/health",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(EndpointCheckFailureKind.Network, result.FailureKind);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Check_WhenHttpStatusIsNotSuccessful_ReturnsHttpStatusFailure(
        HttpStatusCode statusCode)
    {
        HttpMonitoringEndpointChecker checker = CreateChecker((_, _) => Task.FromResult(
            new HttpResponseMessage(statusCode)));

        ServerEndpointCheckResult result = await checker.CheckAsync(
            "https://server.example/health",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(EndpointCheckFailureKind.HttpStatus, result.FailureKind);
    }

    [Theory]
    [InlineData("{\"name\":\"\"}")]
    [InlineData("{\"name\":\"   \"}")]
    [InlineData("{\"other\":\"server\"}")]
    public async Task ServerCheck_WhenNameIsEmptyOrMissing_ReturnsInvalidName(string json)
    {
        HttpMonitoringEndpointChecker checker = CreateChecker((_, _) =>
            Task.FromResult(JsonResponse(json)));

        ServerEndpointCheckResult result = await checker.CheckAsync(
            "https://server.example/health",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(EndpointCheckFailureKind.InvalidName, result.FailureKind);
    }

    [Fact]
    public async Task ServerCheck_WhenNameIsTooLong_ReturnsInvalidName()
    {
        string name = new('a', ModelConstraints.OBJECT_NAME_MAX_LENGTH + 1);
        HttpMonitoringEndpointChecker checker = CreateChecker((_, _) =>
            Task.FromResult(JsonResponse($"{{\"name\":\"{name}\"}}")));

        ServerEndpointCheckResult result = await checker.CheckAsync(
            "https://server.example/health",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(EndpointCheckFailureKind.InvalidName, result.FailureKind);
    }

    [Fact]
    public async Task ServerCheck_WhenJsonIsInvalid_ReturnsInvalidJson()
    {
        HttpMonitoringEndpointChecker checker = CreateChecker((_, _) =>
            Task.FromResult(JsonResponse("{not-json")));

        ServerEndpointCheckResult result = await checker.CheckAsync(
            "https://server.example/health",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(EndpointCheckFailureKind.InvalidJson, result.FailureKind);
    }

    [Theory]
    [InlineData("{\"name\":\"Orders API\"}")]
    [InlineData("{\"name\":\"Orders API\",\"startedAtUtc\":\"not-a-date\"}")]
    [InlineData("{\"name\":\"Orders API\",\"startedAtUtc\":\"2026-08-25T10:00:00\"}")]
    [InlineData("{\"name\":\"Orders API\",\"startedAtUtc\":\"2026-08-25T10:00:00+02:00\"}")]
    [InlineData("{\"name\":\"Orders API\",\"startedAtUtc\":\"2026-08-25T10:00:01Z\"}")]
    public async Task ContainerCheck_WhenStartedAtUtcIsInvalid_ReturnsFailure(string json)
    {
        HttpMonitoringEndpointChecker checker = CreateChecker((_, _) =>
            Task.FromResult(JsonResponse(json)));

        ContainerEndpointCheckResult result = await ((IContainerEndpointChecker)checker).CheckAsync(
            "https://container.example/health",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(EndpointCheckFailureKind.InvalidStartedAtUtc, result.FailureKind);
    }

    private static HttpMonitoringEndpointChecker CreateChecker(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory,
        int timeoutSeconds = 30,
        TimeProvider? timeProvider = null)
    {
        DelegatingHandler handler = new(responseFactory);
        IHttpClientFactory clientFactory = new FakeHttpClientFactory(handler);
        return new HttpMonitoringEndpointChecker(
            clientFactory,
            Options.Create(new MonitoringOptions
            {
                IntervalSeconds = 30,
                TimeoutSeconds = timeoutSeconds,
                FailuresBeforeUnavailable = 3
            }),
            timeProvider ?? new FixedTimeProvider(NOW_UTC));
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(handler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }
    }

    private class DelegatingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return responseFactory(request, cancellationToken);
        }
    }

    private class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
