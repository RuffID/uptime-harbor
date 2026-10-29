using System.Net;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Notifications;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Telegram;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class TelegramHealthTests
{
    [Theory]
    [InlineData(200, 123, "Ready", "Ready")]
    [InlineData(200, 456, "Unavailable", "BotMismatch")]
    [InlineData(401, 123, "Unavailable", "TokenMissing")]
    [InlineData(500, 123, "Unavailable", "TransportUnavailable")]
    [InlineData(429, 123, "Unknown", "Unknown")]
    public async Task ProbeUsesOnlyGetMeAndReportsKnownSafeReason(int code, long botId, string status, string reason)
    {
        // Arrange
        using Handler handler = new(code, botId);
        using HttpClient client = new(handler) { BaseAddress = new Uri("https://telegram.invalid/") };
        NodeReadiness readiness = new(TimeProvider.System);
        TelegramHealthProbe probe = Create(client, readiness, "123:" + new string('x', 35));

        // Act
        await probe.ProbeAsync(TestContext.Current.CancellationToken);
        await probe.ProbeAsync(TestContext.Current.CancellationToken);

        // Assert
        HealthResultAssert(readiness, status, reason);
        Assert.Equal(1, handler.Calls);
        Assert.EndsWith("/getMe", handler.Path);
        Assert.DoesNotContain("sendMessage", handler.Path);
    }

    [Fact]
    public async Task MissingTokenMakesNoRequestAndChatFailureDoesNotChangeGlobalReadiness()
    {
        // Arrange
        using Handler handler = new(200, 123);
        using HttpClient client = new(handler) { BaseAddress = new Uri("https://telegram.invalid/") };
        NodeReadiness readiness = new(TimeProvider.System);
        TelegramHealthProbe probe = Create(client, readiness, string.Empty);

        // Act
        await probe.ProbeAsync(TestContext.Current.CancellationToken);
        HealthResult missing = readiness.Get("Telegram");
        readiness.Report("Telegram", true, "Ready", TimeSpan.FromMinutes(1));
        probe.ReportDeliveryFailure(NotificationDeliveryFailure.Forbidden);
        probe.ReportDeliveryFailure(NotificationDeliveryFailure.RateLimited);

        // Assert
        Assert.Equal(0, handler.Calls);
        Assert.Equal("Unavailable", missing.Status);
        Assert.Equal("TokenMissing", missing.Reason);
        HealthResultAssert(readiness, "Ready", "Ready");
    }

    private static TelegramHealthProbe Create(HttpClient client, NodeReadiness readiness, string token)
    {
        return new(new Factory(client), Options.Create(new TelegramOptions { BotToken = token }),
            Options.Create(new ClusterOptions { ExpectedBotId = 123 }), readiness, TimeProvider.System);
    }

    private static void HealthResultAssert(NodeReadiness readiness, string status, string reason)
    {
        Assert.Equal(status, readiness.Get("Telegram").Status);
        Assert.Equal(reason, readiness.Get("Telegram").Reason);
        Assert.DoesNotContain("xxx", readiness.Get("Telegram").ToString());
    }

    private class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return client;
        }
    }

    private class Handler(int status, long botId) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string Path { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent($"{{\"ok\":true,\"result\":{{\"id\":{botId}}}}}")
            });
        }
    }
}
