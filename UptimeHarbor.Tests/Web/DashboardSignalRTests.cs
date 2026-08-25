using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Monitoring.Models;
using UptimeHarbor.Application.Visibility;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Web.Controllers;
using UptimeHarbor.Web.Hubs;
using UptimeHarbor.Web.Hubs.Models;
using Xunit;

namespace UptimeHarbor.Tests.Web;

public class DashboardSignalRTests
{
    [Fact]
    public void DashboardControllerAndMonitoringHub_RequireAuthentication()
    {
        Assert.Single(typeof(DashboardController).GetCustomAttributes<AuthorizeAttribute>());
        Assert.Single(typeof(MonitoringHub).GetCustomAttributes<AuthorizeAttribute>());
    }

    [Fact]
    public async Task MonitoringHandler_SendsOnlyToResolvedUsersWithMinimalPayload()
    {
        Guid firstUserId = Guid.NewGuid();
        Guid secondUserId = Guid.NewGuid();
        FakeRecipientResolver resolver = new([firstUserId, secondUserId]);
        RecordingClientProxy proxy = new();
        RecordingHubClients clients = new(proxy);
        SignalRMonitoringEventHandler handler = new(
            resolver,
            new FakeHubContext(clients),
            NullLogger<SignalRMonitoringEventHandler>.Instance);
        MonitoringStateChangedEvent monitoringEvent = CreateEvent();

        await handler.HandleAsync(monitoringEvent, TestContext.Current.CancellationToken);

        Assert.Equal(
            [firstUserId.ToString(), secondUserId.ToString()],
            clients.LastUserIds);
        Assert.Equal("MonitoringObjectChanged", proxy.LastMethod);
        MonitoringObjectChangedMessage message = Assert.IsType<MonitoringObjectChangedMessage>(
            Assert.Single(proxy.LastArguments!));
        Assert.Equal(monitoringEvent.ObjectId, message.ObjectId);
        Assert.Equal(monitoringEvent.ObjectKind, message.ObjectKind);
        Assert.Equal(2, typeof(MonitoringObjectChangedMessage).GetProperties().Length);
    }

    [Fact]
    public async Task MonitoringHandler_WhenSignalRThrows_DoesNotPropagateIntoMonitoringFlow()
    {
        SignalRMonitoringEventHandler handler = new(
            new FakeRecipientResolver([Guid.NewGuid()]),
            new FakeHubContext(new RecordingHubClients(new ThrowingClientProxy())),
            NullLogger<SignalRMonitoringEventHandler>.Instance);

        Exception? exception = await Record.ExceptionAsync(() =>
            handler.HandleAsync(CreateEvent(), TestContext.Current.CancellationToken));

        Assert.Null(exception);
    }

    [Fact]
    public async Task ObjectAdministrationChange_SendsDataFreeSnapshotRefreshSignal()
    {
        RecordingClientProxy proxy = new();
        SignalRObjectAdministrationChangePublisher publisher = new(
            new FakeHubContext(new RecordingHubClients(proxy)));

        await publisher.PublishObjectsChangedAsync(TestContext.Current.CancellationToken);

        Assert.Equal("ObjectAdministrationChanged", proxy.LastMethod);
        Assert.Empty(proxy.LastArguments!);
    }

    [Fact]
    public void DashboardClient_ReconnectAndAdministrationEventsReloadFullSnapshot()
    {
        string sourcePath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "UptimeHarbor.Web",
                "wwwroot",
                "ts",
                "pages",
                "dashboard.ts"));
        string source = File.ReadAllText(sourcePath);

        Assert.Contains("hubConnection.onreconnected", source, StringComparison.Ordinal);
        Assert.Contains("ObjectAdministrationChanged", source, StringComparison.Ordinal);
        Assert.Contains("void loadSnapshot(false)", source, StringComparison.Ordinal);
        Assert.Contains("withAutomaticReconnect", source, StringComparison.Ordinal);
        Assert.DoesNotContain("location.reload", source, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", source, StringComparison.Ordinal);
    }

    private static MonitoringStateChangedEvent CreateEvent()
    {
        return new MonitoringStateChangedEvent(
            Guid.NewGuid(),
            MonitoringObjectKind.Container,
            Guid.NewGuid(),
            "container",
            "secret description not sent",
            MonitoringStatus.Available,
            0,
            new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero),
            MonitoringTransition.BecameAvailable,
            true,
            null);
    }

    private class FakeRecipientResolver(IReadOnlyList<Guid> userIds)
        : IMonitoringEventRecipientResolver
    {
        public Task<IReadOnlyList<Guid>> GetRecipientUserIdsAsync(
            MonitoringObjectKind objectKind,
            Guid objectId,
            Guid? serverId,
            CancellationToken cancellationToken = default) => Task.FromResult(userIds);
    }

    private class FakeHubContext(IHubClients clients) : IHubContext<MonitoringHub>
    {
        public IHubClients Clients { get; } = clients;
        public IGroupManager Groups { get; } = new FakeGroupManager();
    }

    private class RecordingHubClients(IClientProxy proxy) : IHubClients
    {
        public IReadOnlyList<string> LastUserIds { get; private set; } = [];
        public IClientProxy All => proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => proxy;
        public IClientProxy Client(string connectionId) => proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => proxy;
        public IClientProxy Group(string groupName) => proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => proxy;
        public IClientProxy User(string userId) => proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds)
        {
            LastUserIds = userIds;
            return proxy;
        }
    }

    private class RecordingClientProxy : IClientProxy
    {
        public string? LastMethod { get; private set; }
        public object?[]? LastArguments { get; private set; }

        public Task SendCoreAsync(
            string method,
            object?[] args,
            CancellationToken cancellationToken = default)
        {
            LastMethod = method;
            LastArguments = args;
            return Task.CompletedTask;
        }
    }

    private class ThrowingClientProxy : IClientProxy
    {
        public Task SendCoreAsync(
            string method,
            object?[] args,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("SignalR unavailable.");
    }

    private class FakeGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
