using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using System.Reflection;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Monitoring.Models;
using UptimeHarbor.Application.Visibility;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.ObjectAdministration;
using UptimeHarbor.Application.ObjectAdministration.Models;
using UptimeHarbor.Application.Security;
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
            new FakeHubContext(new RecordingHubClients(proxy)),
            NullLogger<SignalRObjectAdministrationChangePublisher>.Instance);

        await publisher.PublishObjectsChangedAsync(TestContext.Current.CancellationToken);

        Assert.Equal("ObjectAdministrationChanged", proxy.LastMethod);
        Assert.Empty(proxy.LastArguments!);
    }

    [Fact]
    public async Task ObjectAdministrationChange_WhenSignalRThrows_IsLoggedAndDoesNotPropagate()
    {
        RecordingLogger<SignalRObjectAdministrationChangePublisher> logger = new();
        SignalRObjectAdministrationChangePublisher publisher = new(
            new FakeHubContext(new RecordingHubClients(new ThrowingClientProxy())),
            logger);

        Exception? exception = await Record.ExceptionAsync(() =>
            publisher.PublishObjectsChangedAsync(TestContext.Current.CancellationToken));

        Assert.Null(exception);
        Assert.Equal(LogLevel.Error, Assert.Single(logger.Levels));
    }

    [Fact]
    public async Task ObjectAdministrationSave_WhenSignalRThrows_ReturnsCommittedSuccessWithoutRetry()
    {
        RecordingLogger<SignalRObjectAdministrationChangePublisher> logger = new();
        SignalRObjectAdministrationChangePublisher publisher = new(
            new FakeHubContext(new RecordingHubClients(new ThrowingClientProxy())),
            logger);
        FakeObjectAdministrationUnitOfWork unitOfWork = new();
        ObjectAdministrationService service = new(
            new AdministratorAccessContext(),
            unitOfWork,
            publisher);

        ServiceResult<AdminServerDto> result = await service.CreateServerAsync(
            "https://server.example/health",
            null,
            true,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(1, unitOfWork.SaveCount);
        Assert.Single(unitOfWork.Servers.Items);
        Assert.Equal(LogLevel.Error, Assert.Single(logger.Levels));
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
            null,
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

    private class RecordingLogger<T> : ILogger<T>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Levels.Add(logLevel);
        }
    }

    private class AdministratorAccessContext : IUserAccessContext
    {
        public bool IsAuthenticated => true;
        public bool IsAdministrator => true;
        public bool IsEnabled => true;
        public Guid UserId { get; } = Guid.NewGuid();
    }

    private class FakeObjectAdministrationUnitOfWork : IObjectAdministrationUnitOfWork
    {
        public FakeServerRepository Servers { get; } = new();
        public IMonitoredServerRepository MonitoredServers => Servers;
        public IMonitoredContainerRepository MonitoredContainers { get; } = new EmptyContainerRepository();
        public int SaveCount { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.FromResult(1);
        }

        public async Task ExecuteInTransactionAsync(
            Func<CancellationToken, Task> action,
            CancellationToken cancellationToken = default)
        {
            await action(cancellationToken);
            await SaveChangesAsync(cancellationToken);
        }
    }

    private class FakeServerRepository : IMonitoredServerRepository
    {
        public List<MonitoredServer> Items { get; } = [];
        public Task<List<MonitoredServer>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(Items.ToList());
        public Task<List<MonitoredServer>> GetEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult(Items.Where(item => item.IsMonitoringEnabled).ToList());
        public Task<MonitoredServer?> GetItemByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Items.SingleOrDefault(item => item.Id == id));
        public void Create(MonitoredServer item) => Items.Add(item);
        public void CreateRange(IEnumerable<MonitoredServer> entities) => Items.AddRange(entities);
        public void Delete(MonitoredServer item) => Items.Remove(item);
        public void DeleteRange(IEnumerable<MonitoredServer> entities)
        {
            foreach (MonitoredServer entity in entities.ToList())
            {
                Items.Remove(entity);
            }
        }
    }

    private class EmptyContainerRepository : IMonitoredContainerRepository
    {
        public Task<List<MonitoredContainer>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<List<MonitoredContainer>>([]);
        public Task<List<MonitoredContainer>> GetEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult<List<MonitoredContainer>>([]);
        public Task<List<MonitoredContainer>> GetByServerIdAsync(Guid serverId, CancellationToken cancellationToken = default) => Task.FromResult<List<MonitoredContainer>>([]);
        public Task<MonitoredContainer?> GetItemByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<MonitoredContainer?>(null);
        public void Create(MonitoredContainer item) => throw new NotSupportedException();
        public void CreateRange(IEnumerable<MonitoredContainer> entities) => throw new NotSupportedException();
        public void Delete(MonitoredContainer item) => throw new NotSupportedException();
        public void DeleteRange(IEnumerable<MonitoredContainer> entities) => throw new NotSupportedException();
    }
}
