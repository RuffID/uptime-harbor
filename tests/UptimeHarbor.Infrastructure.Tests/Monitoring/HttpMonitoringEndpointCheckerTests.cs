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
    public void PrimaryHandler_DisablesAutomaticRedirects()
    {
        using HttpClientHandler handler = Assert.IsType<HttpClientHandler>(
            HttpMonitoringEndpointChecker.CreatePrimaryHandler());

        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task ServerCheck_WhenResponseIsValid_ReturnsNameAndUsesAnonymousGet()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        HttpMonitoringEndpointChecker checker = CreateChecker((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(JsonResponse("{\"name\":\" server-01 \",\"startedAtUtc\":\"2026-08-24T08:00:00Z\"}"));
        });

        // Act
        ServerEndpointCheckResult result = await checker.CheckAsync(
            "https://server.example/health",
            TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("server-01", result.Name);
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 8, 0, 0, TimeSpan.Zero), result.StartedAtUtc);
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

    [Fact]
    public async Task Check_WhenContentLengthExceedsLimit_ReturnsResponseTooLargeWithoutReading()
    {
        HttpResponseMessage response = JsonResponse("{\"name\":\"server\"}");
        response.Content.Headers.ContentLength =
            HttpMonitoringEndpointChecker.MAXIMUM_RESPONSE_BODY_BYTES + 1;
        HttpMonitoringEndpointChecker checker = CreateChecker((_, _) => Task.FromResult(response));

        ServerEndpointCheckResult result = await checker.CheckAsync(
            "https://server.example/health",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(EndpointCheckFailureKind.ResponseTooLarge, result.FailureKind);
    }

    [Fact]
    public async Task Check_WhenBodyWithoutLengthExceedsLimit_ReturnsResponseTooLarge()
    {
        byte[] body = new byte[HttpMonitoringEndpointChecker.MAXIMUM_RESPONSE_BODY_BYTES + 1];
        HttpResponseMessage response = StreamResponse(new NonSeekableReadStream(body));
        HttpMonitoringEndpointChecker checker = CreateChecker((_, _) => Task.FromResult(response));

        ServerEndpointCheckResult result = await checker.CheckAsync(
            "https://server.example/health",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(EndpointCheckFailureKind.ResponseTooLarge, result.FailureKind);
    }

    [Fact]
    public async Task Check_WhenChunkedBodyExceedsLimit_ReturnsResponseTooLarge()
    {
        byte[] body = new byte[HttpMonitoringEndpointChecker.MAXIMUM_RESPONSE_BODY_BYTES + 1];
        HttpResponseMessage response = StreamResponse(new NonSeekableReadStream(body, maximumChunkSize: 257));
        response.Headers.TransferEncodingChunked = true;
        HttpMonitoringEndpointChecker checker = CreateChecker((_, _) => Task.FromResult(response));

        ServerEndpointCheckResult result = await checker.CheckAsync(
            "https://server.example/health",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(EndpointCheckFailureKind.ResponseTooLarge, result.FailureKind);
    }

    [Fact]
    public async Task Check_WhenRedirectPointsToAnotherHost_DoesNotFollowRedirect()
    {
        int callCount = 0;
        HttpMonitoringEndpointChecker checker = CreateChecker((_, _) =>
        {
            callCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://other.example/health") }
            });
        });

        ServerEndpointCheckResult result = await checker.CheckAsync(
            "https://server.example/health",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, callCount);
        Assert.Equal(EndpointCheckFailureKind.HttpStatus, result.FailureKind);
    }

    [Fact]
    public async Task Check_WhenRedirectLoops_DoesNotFollowRedirect()
    {
        int callCount = 0;
        const string endpoint = "https://server.example/health";
        HttpMonitoringEndpointChecker checker = CreateChecker((_, _) =>
        {
            callCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PermanentRedirect)
            {
                Headers = { Location = new Uri(endpoint) }
            });
        });

        ServerEndpointCheckResult result = await checker.CheckAsync(
            endpoint,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, callCount);
        Assert.Equal(EndpointCheckFailureKind.HttpStatus, result.FailureKind);
    }

    [Fact]
    public async Task Check_WhenCancelledDuringBodyRead_PropagatesCancellation()
    {
        BlockingReadStream stream = new();
        HttpMonitoringEndpointChecker checker = CreateChecker((_, _) =>
            Task.FromResult(StreamResponse(stream)));
        using CancellationTokenSource cancellationSource = new();

        Task<ServerEndpointCheckResult> check = checker.CheckAsync(
            "https://server.example/health",
            cancellationSource.Token);
        await stream.ReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
    }

    [Fact]
    public async Task Check_WhenTimeoutExpiresDuringBodyRead_ReturnsTimeout()
    {
        BlockingReadStream stream = new();
        HttpMonitoringEndpointChecker checker = CreateChecker(
            (_, _) => Task.FromResult(StreamResponse(stream)),
            timeoutSeconds: 1,
            TimeProvider.System);

        ServerEndpointCheckResult result = await checker.CheckAsync(
            "https://server.example/health",
            TestContext.Current.CancellationToken);

        Assert.Equal(EndpointCheckFailureKind.Timeout, result.FailureKind);
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

    [Theory]
    [InlineData("2026-08-25T08:00:00Z", 8)]
    [InlineData("2026-08-25T08:00:00+00:00", 8)]
    [InlineData("2026-08-25T10:00:00Z", 10)]
    public async Task ServerCheck_WhenBootTimeHasExplicitUtcOffset_AcceptsIt(string startedAtUtc, int expectedHour)
    {
        // Arrange
        string json = "{\"name\":\"server\",\"startedAtUtc\":\"" + startedAtUtc + "\"}";
        HttpMonitoringEndpointChecker checker = CreateChecker((_, _) => Task.FromResult(JsonResponse(json)));

        // Act
        ServerEndpointCheckResult result = await checker.CheckAsync(
            "https://server.example/health", TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(new DateTimeOffset(2026, 8, 25, expectedHour, 0, 0, TimeSpan.Zero), result.StartedAtUtc);
        Assert.Equal(TimeSpan.Zero, result.StartedAtUtc!.Value.Offset);
    }

    [Theory]
    [InlineData("{\"name\":\"server\"}")]
    [InlineData("{\"name\":\"server\",\"startedAtUtc\":null}")]
    [InlineData("{\"name\":\"server\",\"startedAtUtc\":123}")]
    [InlineData("{\"name\":\"server\",\"startedAtUtc\":\"not-a-date\"}")]
    [InlineData("{\"name\":\"server\",\"startedAtUtc\":\"2026-08-25T08:00:00\"}")]
    [InlineData("{\"name\":\"server\",\"startedAtUtc\":\"2026-08-25T08:00:00+07:00\"}")]
    [InlineData("{\"name\":\"server\",\"startedAtUtc\":\"2026-08-25T10:00:01Z\"}")]
    public async Task ServerCheck_WhenBootTimeIsMissingOrInvalid_ReturnsFailure(string json)
    {
        // Arrange
        HttpMonitoringEndpointChecker checker = CreateChecker((_, _) => Task.FromResult(JsonResponse(json)));

        // Act
        ServerEndpointCheckResult result = await checker.CheckAsync(
            "https://server.example/health", TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(EndpointCheckFailureKind.InvalidStartedAtUtc, result.FailureKind);
        Assert.Null(result.StartedAtUtc);
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

    private static HttpResponseMessage StreamResponse(Stream stream)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
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

    private class NonSeekableReadStream(byte[] content, int maximumChunkSize = int.MaxValue)
        : Stream
    {
        private int position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = Math.Min(Math.Min(count, maximumChunkSize), content.Length - position);
            content.AsSpan(position, read).CopyTo(buffer.AsSpan(offset, read));
            position += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = Math.Min(Math.Min(buffer.Length, maximumChunkSize), content.Length - position);
            content.AsMemory(position, read).CopyTo(buffer);
            position += read;
            return ValueTask.FromResult(read);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private class BlockingReadStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
