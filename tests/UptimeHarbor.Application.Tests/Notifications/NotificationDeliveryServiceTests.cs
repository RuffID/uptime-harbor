using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Monitoring.Models;
using UptimeHarbor.Application.Notifications.Models;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Domain.Notifications;
using Xunit;

namespace UptimeHarbor.Tests.Application.Notifications;

public class NotificationDeliveryServiceTests
{
    [Fact]
    public async Task UnassignedContainer_PreparesDeliveryWithoutParentServer()
    {
        // Arrange
        NotificationTestData data = new();
        MonitoredContainer container = new(Guid.NewGuid(), null, "https://api.example/health", null, true);
        TelegramChat chat = data.AddChat(-1001, "API");
        data.Bindings.Items.Add(NotificationBinding.ForContainer(Guid.NewGuid(), chat.Id, container.Id));

        // Act
        IReadOnlyList<PendingNotificationDeliveryDto> deliveries =
            await data.DeliveryService.GetPendingDeliveriesAsync(
                CreateEvent(container, Guid.NewGuid(), MonitoringStatus.Unavailable),
                TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(deliveries);
    }

    [Fact]
    public async Task Outage_DeliversToEveryBoundChatButNotUnboundChat()
    {
        NotificationTestData data = new();
        MonitoredServer server = data.AddServer("server-01");
        TelegramChat firstChat = data.AddChat(-1001, "Первый");
        TelegramChat secondChat = data.AddChat(-1002, "Второй");
        data.AddChat(-1003, "Непривязанный");
        data.Bindings.Items.Add(NotificationBinding.ForServer(Guid.NewGuid(), firstChat.Id, server.Id));
        data.Bindings.Items.Add(NotificationBinding.ForServer(Guid.NewGuid(), secondChat.Id, server.Id));
        Guid incidentId = Guid.NewGuid();

        IReadOnlyList<PendingNotificationDeliveryDto> deliveries =
            await data.DeliveryService.GetPendingDeliveriesAsync(
                CreateEvent(server, incidentId, MonitoringStatus.Unavailable),
                TestContext.Current.CancellationToken);

        Assert.Equal(2, deliveries.Count);
        Assert.Equal([firstChat.Id, secondChat.Id], deliveries.Select(item => item.Recipient.Id).ToList());
    }

    [Fact]
    public async Task ReprocessingOutage_DeliversOnlyToPreviouslyUnsuccessfulBinding()
    {
        NotificationTestData data = new();
        MonitoredServer server = data.AddServer("server-01");
        TelegramChat firstChat = data.AddChat(-1001, "Первый");
        TelegramChat secondChat = data.AddChat(-1002, "Второй");
        NotificationBinding first = NotificationBinding.ForServer(Guid.NewGuid(), firstChat.Id, server.Id);
        NotificationBinding second = NotificationBinding.ForServer(Guid.NewGuid(), secondChat.Id, server.Id);
        data.Bindings.Items.AddRange([first, second]);
        Guid incidentId = Guid.NewGuid();
        first.MarkOutageDelivered(incidentId);

        IReadOnlyList<PendingNotificationDeliveryDto> deliveries =
            await data.DeliveryService.GetPendingDeliveriesAsync(
                CreateEvent(server, incidentId, MonitoringStatus.Unavailable),
                TestContext.Current.CancellationToken);

        PendingNotificationDeliveryDto delivery = Assert.Single(deliveries);
        Assert.Equal(second.Id, delivery.BindingId);
    }

    [Fact]
    public async Task Recovery_DeliversOnlyToBindingsWithSuccessfulOutageForSameIncident()
    {
        NotificationTestData data = new();
        MonitoredServer server = data.AddServer("server-01");
        TelegramChat firstChat = data.AddChat(-1001, "Первый");
        TelegramChat secondChat = data.AddChat(-1002, "Второй");
        NotificationBinding first = NotificationBinding.ForServer(Guid.NewGuid(), firstChat.Id, server.Id);
        NotificationBinding second = NotificationBinding.ForServer(Guid.NewGuid(), secondChat.Id, server.Id);
        data.Bindings.Items.AddRange([first, second]);
        Guid incidentId = Guid.NewGuid();
        first.MarkOutageDelivered(incidentId);

        IReadOnlyList<PendingNotificationDeliveryDto> deliveries =
            await data.DeliveryService.GetPendingDeliveriesAsync(
                CreateEvent(server, incidentId, MonitoringStatus.Available),
                TestContext.Current.CancellationToken);

        PendingNotificationDeliveryDto delivery = Assert.Single(deliveries);
        Assert.Equal(first.Id, delivery.BindingId);
        Assert.Equal(NotificationDeliveryKind.Recovery, delivery.Kind);
    }

    [Fact]
    public async Task SuccessfulRecovery_IsNotRepeated()
    {
        NotificationTestData data = new();
        MonitoredServer server = data.AddServer("server-01");
        TelegramChat chat = data.AddChat(-1001, "Первый");
        NotificationBinding binding = NotificationBinding.ForServer(Guid.NewGuid(), chat.Id, server.Id);
        data.Bindings.Items.Add(binding);
        Guid incidentId = Guid.NewGuid();
        binding.MarkOutageDelivered(incidentId);
        binding.MarkRecoveryDelivered(incidentId);

        IReadOnlyList<PendingNotificationDeliveryDto> deliveries =
            await data.DeliveryService.GetPendingDeliveriesAsync(
                CreateEvent(server, incidentId, MonitoringStatus.Available),
                TestContext.Current.CancellationToken);

        Assert.Empty(deliveries);
    }

    [Fact]
    public async Task FirstSecondFailuresAndManualDisable_DoNotCreateDeliveries()
    {
        NotificationTestData data = new();
        MonitoredServer server = data.AddServer();
        TelegramChat chat = data.AddChat(-1001, "Первый");
        data.Bindings.Items.Add(NotificationBinding.ForServer(Guid.NewGuid(), chat.Id, server.Id));

        IReadOnlyList<PendingNotificationDeliveryDto> failureDeliveries =
            await data.DeliveryService.GetPendingDeliveriesAsync(
                CreateEvent(server, null, MonitoringStatus.Unknown),
                TestContext.Current.CancellationToken);
        IReadOnlyList<PendingNotificationDeliveryDto> disabledDeliveries =
            await data.DeliveryService.GetPendingDeliveriesAsync(
                CreateEvent(server, null, MonitoringStatus.Unknown, MonitoringTransition.MonitoringDisabled),
                TestContext.Current.CancellationToken);

        Assert.Empty(failureDeliveries);
        Assert.Empty(disabledDeliveries);
    }

    [Fact]
    public async Task ServerAndContainerBindings_AreIndependent()
    {
        NotificationTestData data = new();
        MonitoredServer server = data.AddServer("server-01");
        MonitoredContainer container = data.AddContainer(server, "app-01");
        TelegramChat serverChat = data.AddChat(-1001, "Серверы");
        TelegramChat containerChat = data.AddChat(-1002, "Контейнеры");
        data.Bindings.Items.Add(NotificationBinding.ForServer(Guid.NewGuid(), serverChat.Id, server.Id));
        data.Bindings.Items.Add(NotificationBinding.ForContainer(Guid.NewGuid(), containerChat.Id, container.Id));

        IReadOnlyList<PendingNotificationDeliveryDto> deliveries =
            await data.DeliveryService.GetPendingDeliveriesAsync(
                CreateEvent(container, Guid.NewGuid(), MonitoringStatus.Unavailable),
                TestContext.Current.CancellationToken);

        PendingNotificationDeliveryDto delivery = Assert.Single(deliveries);
        Assert.Equal(containerChat.Id, delivery.Recipient.Id);
        Assert.Contains("Родительский сервер: Server 0", delivery.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContainerRecovery_UsesFallbackUtcAndTimeProviderUptime()
    {
        DateTimeOffset nowUtc = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        NotificationTestData data = new(nowUtc: nowUtc);
        MonitoredServer server = data.AddServer();
        MonitoredContainer container = data.AddContainer(server);
        TelegramChat chat = data.AddChat(-1001, "Контейнеры");
        NotificationBinding binding = NotificationBinding.ForContainer(Guid.NewGuid(), chat.Id, container.Id);
        data.Bindings.Items.Add(binding);
        Guid incidentId = Guid.NewGuid();
        binding.MarkOutageDelivered(incidentId);

        IReadOnlyList<PendingNotificationDeliveryDto> deliveries =
            await data.DeliveryService.GetPendingDeliveriesAsync(
                CreateEvent(container, incidentId, MonitoringStatus.Available),
                TestContext.Current.CancellationToken);

        string message = Assert.Single(deliveries).Message;
        Assert.Contains(container.Id.ToString("N")[..8], message, StringComparison.Ordinal);
        Assert.Contains(server.Name, message, StringComparison.Ordinal);
        Assert.Contains("2026-08-25T12:00:00.0000000+00:00", message, StringComparison.Ordinal);
        Assert.Contains("Uptime: 02:00:00", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonUtcEvent_IsRejected()
    {
        NotificationTestData data = new();
        MonitoredServer server = data.AddServer();
        MonitoringStateChangedEvent monitoringEvent = CreateEvent(
            server,
            Guid.NewGuid(),
            MonitoringStatus.Unavailable) with
        {
            CheckedAtUtc = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.FromHours(3))
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            data.DeliveryService.GetPendingDeliveriesAsync(
                monitoringEvent,
                TestContext.Current.CancellationToken));
    }

    private static MonitoringStateChangedEvent CreateEvent(
        MonitoredServer server,
        Guid? incidentId,
        MonitoringStatus status,
        MonitoringTransition transition = MonitoringTransition.None) =>
        new(
            server.Id,
            MonitoringObjectKind.Server,
            null,
            server.LastKnownName,
            server.Description,
            status,
            incidentId,
            status == MonitoringStatus.Unavailable ? 3 : 0,
            new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero),
            null,
            transition,
            status == MonitoringStatus.Available,
            null);

    private static MonitoringStateChangedEvent CreateEvent(
        MonitoredContainer container,
        Guid incidentId,
        MonitoringStatus status) =>
        new(
            container.Id,
            MonitoringObjectKind.Container,
            container.ServerId,
            container.LastKnownName,
            container.Description,
            status,
            incidentId,
            status == MonitoringStatus.Unavailable ? 3 : 0,
            new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero),
            status == MonitoringStatus.Unavailable
                ? MonitoringTransition.BecameUnavailable
                : MonitoringTransition.Recovered,
            status == MonitoringStatus.Available,
            null);
}
