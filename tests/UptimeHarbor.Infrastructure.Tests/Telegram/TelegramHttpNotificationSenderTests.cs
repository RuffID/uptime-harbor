using System.Net;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Notifications;
using UptimeHarbor.Application.Notifications.Models;
using UptimeHarbor.Infrastructure.Telegram;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Telegram;

public class TelegramHttpNotificationSenderTests
{
    private const string TEST_TOKEN = "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi";

    [Fact]
    public async Task SendAsync_UsesTelegramHttpContractWithoutExternalConnection()
    {
        // Arrange
        RecordingHandler handler = new(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true}")
        });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.telegram.test/") };
        TelegramHttpNotificationSender sender = new(
            client,
            Options.Create(new TelegramOptions { BotToken = TEST_TOKEN }));

        // Act
        await sender.SendAsync(
            new NotificationRecipient(Guid.NewGuid(), "-100123"),
            "test message",
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal($"https://api.telegram.test/bot{TEST_TOKEN}/sendMessage", handler.RequestUri);
        Assert.Contains("\"chat_id\":-100123", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"text\":\"test message\"", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(TEST_TOKEN, handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_WhenTelegramFails_DoesNotExposeTokenOrResponseContent()
    {
        // Arrange
        const string token = TEST_TOKEN;
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

        // Act
        NotificationDeliveryException exception = await Assert.ThrowsAsync<NotificationDeliveryException>(() =>
            sender.SendAsync(
                new NotificationRecipient(Guid.NewGuid(), "-100123"),
                "sensitive message",
                TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(NotificationDeliveryFailure.Unavailable, exception.Failure);
        Assert.DoesNotContain(token, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive response body", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive message", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(401, "", NotificationDeliveryFailure.InvalidToken)]
    [InlineData(404, "", NotificationDeliveryFailure.InvalidToken)]
    [InlineData(403, "", NotificationDeliveryFailure.Forbidden)]
    [InlineData(429, "", NotificationDeliveryFailure.RateLimited)]
    [InlineData(400, "{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: chat not found\"}", NotificationDeliveryFailure.ChatNotFound)]
    [InlineData(200, "{\"ok\":false,\"error_code\":401}", NotificationDeliveryFailure.InvalidToken)]
    [InlineData(200, "{}", NotificationDeliveryFailure.InvalidResponse)]
    [InlineData(200, "not JSON", NotificationDeliveryFailure.InvalidResponse)]
    public async Task SendAsync_ClassifiesSafeDeliveryFailure(int status, string body, NotificationDeliveryFailure failure)
    {
        // Arrange
        RecordingHandler handler = new(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body)
        });
        TelegramHttpNotificationSender sender = new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.test/") },
            Options.Create(new TelegramOptions { BotToken = TEST_TOKEN }));

        // Act
        NotificationDeliveryException exception = await Assert.ThrowsAsync<NotificationDeliveryException>(() =>
            sender.SendAsync(new NotificationRecipient(Guid.NewGuid(), "-100123"), "test", TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(failure, exception.Failure);
        Assert.DoesNotContain(TEST_TOKEN, exception.UserMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("000000000:replace-with-bot-token")]
    [InlineData("123456789")]
    [InlineData("123456789:short")]
    [InlineData("123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi\r\n")]
    public async Task SendAsync_RejectsInvalidConfigurationBeforeHttp(string token)
    {
        // Arrange
        RecordingHandler handler = new(new HttpResponseMessage(HttpStatusCode.OK));
        TelegramHttpNotificationSender sender = new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.test/") },
            Options.Create(new TelegramOptions { BotToken = token }));

        // Act
        NotificationDeliveryException exception = await Assert.ThrowsAsync<NotificationDeliveryException>(() =>
            sender.SendAsync(new NotificationRecipient(Guid.NewGuid(), "-100123"), "test", TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(NotificationDeliveryFailure.InvalidConfiguration, exception.Failure);
        Assert.Contains("Telegram:BotToken", exception.UserMessage, StringComparison.Ordinal);
        Assert.Null(handler.RequestUri);
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
