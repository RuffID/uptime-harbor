using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Monitoring.Models;
using UptimeHarbor.Application.Notifications;
using UptimeHarbor.Application.Notifications.Models;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Infrastructure.Telegram;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Telegram;

public class TelegramMonitoringEventHandlerTests
{
    [Fact]
    public async Task Handler_IsolatesOneChatFailureAndMarksOtherChatSuccessful()
    {
        Guid failedChatId = Guid.NewGuid();
        Guid successfulChatId = Guid.NewGuid();
        FakeDeliveryService deliveryService = new(
        [
            CreateDelivery(failedChatId),
            CreateDelivery(successfulChatId)
        ]);
        FakeSender sender = new(failedChatId);
        ServiceCollection services = new();
        services.AddSingleton<INotificationDeliveryService>(deliveryService);
        using ServiceProvider provider = services.BuildServiceProvider();
        TelegramMonitoringEventHandler handler = new(
            deliveryService,
            sender,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TelegramMonitoringEventHandler>.Instance);

        Exception? exception = await Record.ExceptionAsync(() =>
            handler.HandleAsync(CreateEvent(), TestContext.Current.CancellationToken));

        Assert.Null(exception);
        Assert.Equal([failedChatId, successfulChatId], sender.AttemptedRecipientIds);
        Assert.Single(deliveryService.MarkedBindingIds);
    }

    private static PendingNotificationDeliveryDto CreateDelivery(Guid chatId) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            NotificationDeliveryKind.Outage,
            new NotificationRecipient(chatId, "-100123"),
            "message");

    private static MonitoringStateChangedEvent CreateEvent() =>
        new(
            Guid.NewGuid(),
            MonitoringObjectKind.Server,
            null,
            "server",
            null,
            MonitoringStatus.Unavailable,
            Guid.NewGuid(),
            3,
            new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero),
            null,
            MonitoringTransition.BecameUnavailable,
            false,
            EndpointCheckFailureKind.HttpStatus);

    private class FakeSender(Guid failedChatId) : INotificationSender
    {
        public List<Guid> AttemptedRecipientIds { get; } = [];

        public Task SendAsync(
            NotificationRecipient recipient,
            string message,
            CancellationToken cancellationToken = default)
        {
            AttemptedRecipientIds.Add(recipient.Id);
            if (recipient.Id == failedChatId)
            {
                throw new NotificationDeliveryException("failure");
            }
            return Task.CompletedTask;
        }
    }

    private class FakeDeliveryService(IReadOnlyList<PendingNotificationDeliveryDto> deliveries)
        : INotificationDeliveryService
    {
        public List<Guid> MarkedBindingIds { get; } = [];

        public Task<IReadOnlyList<PendingNotificationDeliveryDto>> GetPendingDeliveriesAsync(
            MonitoringStateChangedEvent monitoringEvent,
            CancellationToken cancellationToken = default) => Task.FromResult(deliveries);

        public Task<ServiceResult> MarkDeliverySuccessfulAsync(
            Guid bindingId,
            Guid incidentId,
            NotificationDeliveryKind kind,
            CancellationToken cancellationToken = default)
        {
            MarkedBindingIds.Add(bindingId);
            return Task.FromResult(ServiceResult.Ok());
        }
    }
}
