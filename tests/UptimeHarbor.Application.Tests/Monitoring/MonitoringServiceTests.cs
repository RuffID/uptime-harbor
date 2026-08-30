using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Monitoring.Models;
using UptimeHarbor.Domain.Monitoring;
using Xunit;

namespace UptimeHarbor.Tests.Application.Monitoring;

public class MonitoringServiceTests
{
    private static readonly DateTimeOffset CHECKED_AT_UTC =
        new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ThreeConsecutiveFailures_MakeServerUnavailable()
    {
        TestData data = new();
        MonitoredServer server = data.AddServer(isMonitoringEnabled: true);

        await data.RecordServerFailureAsync(server, CHECKED_AT_UTC);
        await data.RecordServerFailureAsync(server, CHECKED_AT_UTC.AddSeconds(30));
        await data.RecordServerFailureAsync(server, CHECKED_AT_UTC.AddSeconds(60));

        Assert.Equal(MonitoringStatus.Unavailable, server.Status);
        Assert.Equal(3, server.ConsecutiveFailures);
        Assert.Equal(MonitoringTransition.BecameUnavailable, data.Publisher.Events[^1].Transition);
        Assert.Single(data.UnitOfWork.EnqueuedNotifications);
    }

    [Fact]
    public async Task FailureSuccessFailure_DoesNotMakeServerUnavailable()
    {
        TestData data = new();
        MonitoredServer server = data.AddServer(isMonitoringEnabled: true);

        await data.RecordServerFailureAsync(server, CHECKED_AT_UTC);
        await data.Service.RecordServerResultAsync(
            server.Id,
            server.EndpointUrl,
            ServerEndpointCheckResult.Succeeded("server-01", CHECKED_AT_UTC.AddSeconds(30)),
            3,
            TestContext.Current.CancellationToken);
        await data.RecordServerFailureAsync(server, CHECKED_AT_UTC.AddSeconds(60));

        Assert.Equal(MonitoringStatus.Available, server.Status);
        Assert.Equal(1, server.ConsecutiveFailures);
    }

    [Fact]
    public async Task SuccessAfterUnavailable_CreatesRecoveryTransition()
    {
        TestData data = new();
        MonitoredServer server = data.AddServer(isMonitoringEnabled: true);
        await data.RecordServerFailureAsync(server, CHECKED_AT_UTC);
        await data.RecordServerFailureAsync(server, CHECKED_AT_UTC.AddSeconds(30));
        await data.RecordServerFailureAsync(server, CHECKED_AT_UTC.AddSeconds(60));

        await data.Service.RecordServerResultAsync(
            server.Id,
            server.EndpointUrl,
            ServerEndpointCheckResult.Succeeded("server-01", CHECKED_AT_UTC.AddSeconds(90)),
            3,
            TestContext.Current.CancellationToken);

        Assert.Equal(MonitoringStatus.Available, server.Status);
        Assert.Equal(0, server.ConsecutiveFailures);
        Assert.Equal(MonitoringTransition.Recovered, data.Publisher.Events[^1].Transition);
        Assert.Equal(2, data.UnitOfWork.EnqueuedNotifications.Count);
    }

    [Fact]
    public async Task DisabledObject_IsNotReturnedOrUpdated()
    {
        TestData data = new();
        MonitoredServer disabledServer = data.AddServer(isMonitoringEnabled: false);

        IReadOnlyList<MonitoringTargetDto> targets = await data.Service.GetEnabledTargetsAsync(
            TestContext.Current.CancellationToken);
        bool recorded = await data.Service.RecordServerResultAsync(
            disabledServer.Id,
            disabledServer.EndpointUrl,
            ServerEndpointCheckResult.Succeeded("disabled", CHECKED_AT_UTC),
            3,
            TestContext.Current.CancellationToken);

        Assert.Empty(targets);
        Assert.False(recorded);
        Assert.Equal(0, data.UnitOfWork.SaveCount);
        Assert.Empty(data.Publisher.Events);
    }

    [Fact]
    public async Task ServerAndContainer_AreUpdatedIndependently()
    {
        TestData data = new();
        MonitoredServer server = data.AddServer(isMonitoringEnabled: true);
        MonitoredContainer container = data.AddContainer(server, isMonitoringEnabled: true);

        await data.RecordServerFailureAsync(server, CHECKED_AT_UTC);
        await data.RecordServerFailureAsync(server, CHECKED_AT_UTC.AddSeconds(30));
        await data.RecordServerFailureAsync(server, CHECKED_AT_UTC.AddSeconds(60));
        await data.Service.RecordContainerResultAsync(
            container.Id,
            container.EndpointUrl,
            ContainerEndpointCheckResult.Succeeded(
                "Orders API",
                CHECKED_AT_UTC.AddHours(-1),
                CHECKED_AT_UTC),
            3,
            TestContext.Current.CancellationToken);

        Assert.Equal(MonitoringStatus.Unavailable, server.Status);
        Assert.Equal(MonitoringStatus.Available, container.Status);
        Assert.Equal(CHECKED_AT_UTC.AddHours(-1), container.StartedAtUtc);
    }

    [Fact]
    public async Task Result_WhenEndpointChangedDuringCheck_IsIgnored()
    {
        TestData data = new();
        MonitoredServer server = data.AddServer(isMonitoringEnabled: true);
        string oldEndpoint = server.EndpointUrl;
        server.UpdateSettings("https://new-server.example/health", null, true);

        bool recorded = await data.Service.RecordServerResultAsync(
            server.Id,
            oldEndpoint,
            ServerEndpointCheckResult.Succeeded("stale-name", CHECKED_AT_UTC),
            3,
            TestContext.Current.CancellationToken);

        Assert.False(recorded);
        Assert.Null(server.LastKnownName);
        Assert.Equal(0, data.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task Event_IsPublishedOnlyAfterSuccessfulSave()
    {
        List<string> operations = [];
        TestData successfulData = new(operations: operations);
        MonitoredServer server = successfulData.AddServer(isMonitoringEnabled: true);

        await successfulData.Service.RecordServerResultAsync(
            server.Id,
            server.EndpointUrl,
            ServerEndpointCheckResult.Succeeded("server-01", CHECKED_AT_UTC),
            3,
            TestContext.Current.CancellationToken);

        Assert.Equal(["save", "publish"], operations);

        TestData failedData = new(throwOnSave: true);
        MonitoredServer failedServer = failedData.AddServer(isMonitoringEnabled: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failedData.Service.RecordServerResultAsync(
                failedServer.Id,
                failedServer.EndpointUrl,
                ServerEndpointCheckResult.Succeeded("server-02", CHECKED_AT_UTC),
                3,
                TestContext.Current.CancellationToken));
        Assert.Empty(failedData.Publisher.Events);
    }

    private class TestData
    {
        public TestData(bool throwOnSave = false, List<string>? operations = null)
        {
            Servers = new FakeServerRepository();
            Containers = new FakeContainerRepository();
            UnitOfWork = new FakeMonitoringUnitOfWork(
                Servers,
                Containers,
                throwOnSave,
                operations);
            Publisher = new FakeEventPublisher(operations);
            Service = new MonitoringService(UnitOfWork, Publisher);
        }

        public FakeServerRepository Servers { get; }

        public FakeContainerRepository Containers { get; }

        public FakeMonitoringUnitOfWork UnitOfWork { get; }

        public FakeEventPublisher Publisher { get; }

        public MonitoringService Service { get; }

        public MonitoredServer AddServer(bool isMonitoringEnabled)
        {
            MonitoredServer server = new(
                Guid.NewGuid(),
                $"https://server-{Servers.Items.Count}.example/health",
                "Server description",
                isMonitoringEnabled);
            Servers.Items.Add(server);
            return server;
        }

        public MonitoredContainer AddContainer(
            MonitoredServer server,
            bool isMonitoringEnabled)
        {
            MonitoredContainer container = new(
                Guid.NewGuid(),
                server.Id,
                $"https://container-{Containers.Items.Count}.example/health",
                "Container description",
                isMonitoringEnabled);
            Containers.Items.Add(container);
            return container;
        }

        public Task<bool> RecordServerFailureAsync(
            MonitoredServer server,
            DateTimeOffset checkedAtUtc)
        {
            return Service.RecordServerResultAsync(
                server.Id,
                server.EndpointUrl,
                ServerEndpointCheckResult.Failed(
                    EndpointCheckFailureKind.Network,
                    checkedAtUtc),
                3,
                TestContext.Current.CancellationToken);
        }
    }

    private class FakeMonitoringUnitOfWork(
        IMonitoredServerRepository servers,
        IMonitoredContainerRepository containers,
        bool throwOnSave,
        List<string>? operations)
        : IMonitoringUnitOfWork
    {
        public IMonitoredServerRepository MonitoredServers { get; } = servers;

        public IMonitoredContainerRepository MonitoredContainers { get; } = containers;

        public int SaveCount { get; private set; }

        public List<MonitoringStateChangedEvent> EnqueuedNotifications { get; } = [];

        public void EnqueueNotification(MonitoringStateChangedEvent monitoringEvent)
        {
            EnqueuedNotifications.Add(monitoringEvent);
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            operations?.Add("save");
            if (throwOnSave)
            {
                throw new InvalidOperationException("Save failed.");
            }

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

        public Task<IReadOnlyList<MonitoredServer>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MonitoredServer>>(Items.ToList());

        public Task<IReadOnlyList<MonitoredServer>> GetEnabledAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MonitoredServer>>(
                Items.Where(item => item.IsMonitoringEnabled).ToList());

        public Task<MonitoredServer?> GetItemByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.SingleOrDefault(item => item.Id == id));

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

    private class FakeContainerRepository : IMonitoredContainerRepository
    {
        public List<MonitoredContainer> Items { get; } = [];

        public Task<IReadOnlyList<MonitoredContainer>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MonitoredContainer>>(Items.ToList());

        public Task<IReadOnlyList<MonitoredContainer>> GetEnabledAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MonitoredContainer>>(
                Items.Where(item => item.IsMonitoringEnabled).ToList());

        public Task<IReadOnlyList<MonitoredContainer>> GetByServerIdAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MonitoredContainer>>(
                Items.Where(item => item.ServerId == serverId).ToList());

        public Task<MonitoredContainer?> GetItemByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.SingleOrDefault(item => item.Id == id));

        public void Create(MonitoredContainer item) => Items.Add(item);

        public void CreateRange(IEnumerable<MonitoredContainer> entities) => Items.AddRange(entities);

        public void Delete(MonitoredContainer item) => Items.Remove(item);

        public void DeleteRange(IEnumerable<MonitoredContainer> entities)
        {
            foreach (MonitoredContainer entity in entities.ToList())
            {
                Items.Remove(entity);
            }
        }
    }

    private class FakeEventPublisher(List<string>? operations) : IMonitoringEventPublisher
    {
        public List<MonitoringStateChangedEvent> Events { get; } = [];

        public Task PublishAsync(
            MonitoringStateChangedEvent monitoringEvent,
            CancellationToken cancellationToken = default)
        {
            operations?.Add("publish");
            Events.Add(monitoringEvent);
            return Task.CompletedTask;
        }
    }
}
