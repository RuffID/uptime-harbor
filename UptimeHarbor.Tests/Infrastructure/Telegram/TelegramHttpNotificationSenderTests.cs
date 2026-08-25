using System.Net;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Notifications;
using UptimeHarbor.Application.Notifications.Models;
using UptimeHarbor.Infrastructure.Telegram;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Telegram;

public class TelegramHttpNotificationSenderTests
{
    [Fact]
    public async Task SendAsync_UsesTelegramHttpContractWithoutExternalConnection()
    {
        RecordingHandler handler = new(new HttpResponseMessage(HttpStatusCode.OK));
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.telegram.test/") };
        TelegramHttpNotificationSender sender = new(
            client,
            Options.Create(new TelegramOptions { BotToken = "secret-token" }));

        await sender.SendAsync(
            new NotificationRecipient(Guid.NewGuid(), "-100123"),
            "test message",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://api.telegram.test/botsecret-token/sendMessage", handler.RequestUri);
        Assert.Contains("\"chat_id\":-100123", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"text\":\"test message\"", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_WhenTelegramFails_DoesNotExposeTokenOrResponseContent()
    {
        const string token = "secret-token";
        HttpResponseMessage response = new(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("sensitive response body")
        };
        TelegramHttpNotificationSender sender = new(
            new HttpClient(new RecordingHandler(response))
            {
                BaseAddress = new Uri("https://api.telegram.test/")
            },
            Options.Create(new TelegramOptions { BotToken = token }));

        NotificationDeliveryException exception = await Assert.ThrowsAsync<NotificationDeliveryException>(() =>
            sender.SendAsync(
                new NotificationRecipient(Guid.NewGuid(), "-100123"),
                "sensitive message",
                TestContext.Current.CancellationToken));

        Assert.DoesNotContain(token, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive response body", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive message", exception.ToString(), StringComparison.Ordinal);
    }

    private class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? RequestUri { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri?.ToString();
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }
}
