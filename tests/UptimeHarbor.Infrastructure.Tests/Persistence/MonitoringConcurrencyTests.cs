using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Monitoring.Models;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Infrastructure.Persistence;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Persistence;

public class MonitoringConcurrencyTests
{
    private static readonly DateTimeOffset CHECKED_AT_UTC =
        new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MonitoringAgainstDisable_RejectsStaleMonitoringWrite()
    {
        // Arrange
        await using SqliteConnection connection = await OpenDatabaseAsync();
        Guid serverId = await AddServerAsync(connection);
        await using UptimeHarborDbContext monitoringContext = CreateContext(connection);
        await using UptimeHarborDbContext administrationContext = CreateContext(connection);
        MonitoredServer monitoringServer = await monitoringContext.MonitoredServers.SingleAsync(
            TestContext.Current.CancellationToken);
        MonitoredServer administrationServer = await administrationContext.MonitoredServers.SingleAsync(
            TestContext.Current.CancellationToken);

        administrationServer.SetMonitoringEnabled(false);
        await administrationContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        monitoringServer.RecordSuccessfulCheck("stale", CHECKED_AT_UTC.AddDays(-1), CHECKED_AT_UTC);

        // Act
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            monitoringContext.SaveChangesAsync(TestContext.Current.CancellationToken));

        // Assert
        await using UptimeHarborDbContext verificationContext = CreateContext(connection);
        MonitoredServer stored = await verificationContext.MonitoredServers.SingleAsync(
            server => server.Id == serverId,
            TestContext.Current.CancellationToken);
        Assert.False(stored.IsMonitoringEnabled);
        Assert.Equal(MonitoringStatus.Unknown, stored.Status);
        Assert.Null(stored.LastKnownName);
    }

    [Fact]
    public async Task MonitoringAgainstEndpointChange_RejectsStaleMonitoringWrite()
    {
        // Arrange
        await using SqliteConnection connection = await OpenDatabaseAsync();
        await AddServerAsync(connection);
        await using UptimeHarborDbContext monitoringContext = CreateContext(connection);
        await using UptimeHarborDbContext administrationContext = CreateContext(connection);
        MonitoredServer monitoringServer = await monitoringContext.MonitoredServers.SingleAsync(
            TestContext.Current.CancellationToken);
        MonitoredServer administrationServer = await administrationContext.MonitoredServers.SingleAsync(
            TestContext.Current.CancellationToken);

        administrationServer.UpdateSettings("https://new.example/health", null, true);
        await administrationContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        monitoringServer.RecordSuccessfulCheck("stale", CHECKED_AT_UTC.AddDays(-1), CHECKED_AT_UTC);

        // Act
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            monitoringContext.SaveChangesAsync(TestContext.Current.CancellationToken));

        // Assert
        await using UptimeHarborDbContext verificationContext = CreateContext(connection);
        MonitoredServer stored = await verificationContext.MonitoredServers.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("https://new.example/health", stored.EndpointUrl);
        Assert.Null(stored.LastKnownName);
    }

    [Fact]
    public async Task ContainerMonitoringAgainstDisable_RejectsStaleMonitoringWrite()
    {
        // Arrange
        await using SqliteConnection connection = await OpenDatabaseAsync();
        Guid serverId = await AddServerAsync(connection);
        Guid containerId = Guid.NewGuid();
        await using (UptimeHarborDbContext setupContext = CreateContext(connection))
        {
            setupContext.MonitoredServers.Add(MonitoredServer.CreateApplication(
                containerId,
                serverId,
                "https://container.example/health",
                null,
                true));
            await setupContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act
        await using UptimeHarborDbContext monitoringContext = CreateContext(connection);
        await using UptimeHarborDbContext administrationContext = CreateContext(connection);
        MonitoredServer monitoringContainer = await monitoringContext.MonitoredServers.SingleAsync(
            item => item.Id == containerId,
            TestContext.Current.CancellationToken);
        MonitoredServer administrationContainer = await administrationContext.MonitoredServers.SingleAsync(
            item => item.Id == containerId,
            TestContext.Current.CancellationToken);
        administrationContainer.SetMonitoringEnabled(false);
        await administrationContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        monitoringContainer.RecordSuccessfulCheck(
            "stale",
            CHECKED_AT_UTC.AddHours(-1),
            CHECKED_AT_UTC);

        // Assert
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            monitoringContext.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TwoAdministrativeUpdates_SecondUpdateConflicts()
    {
        await using SqliteConnection connection = await OpenDatabaseAsync();
        await AddServerAsync(connection);
        await using UptimeHarborDbContext firstContext = CreateContext(connection);
        await using UptimeHarborDbContext secondContext = CreateContext(connection);
        MonitoredServer first = await firstContext.MonitoredServers.SingleAsync(
            TestContext.Current.CancellationToken);
        MonitoredServer second = await secondContext.MonitoredServers.SingleAsync(
            TestContext.Current.CancellationToken);

        first.UpdateSettings("https://first.example/health", null, true);
        second.UpdateSettings("https://second.example/health", null, true);
        await firstContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            secondContext.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MonitoringConflict_DoesNotPublishSignalROrPersistTelegramOutboxJob()
    {
        await using SqliteConnection connection = await OpenDatabaseAsync();
        Guid serverId = await AddServerAsync(connection);
        await using UptimeHarborDbContext monitoringContext = CreateContext(connection);
        await using UptimeHarborDbContext administrationContext = CreateContext(connection);
        ProviderServerRepository serverRepository = new(
            monitoringContext,
            async () =>
            {
                MonitoredServer server = await administrationContext.MonitoredServers.SingleAsync(
                    TestContext.Current.CancellationToken);
                server.SetMonitoringEnabled(false);
                await administrationContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            });
        ProviderMonitoringUnitOfWork unitOfWork = new(monitoringContext, serverRepository);
        RecordingMonitoringPublisher publisher = new();
        MonitoringService service = new(unitOfWork, publisher);

        bool recorded = await service.RecordServerResultAsync(
            serverId,
            "https://server.example/health",
            ServerEndpointCheckResult.Failed(EndpointCheckFailureKind.Network, CHECKED_AT_UTC),
            failuresBeforeUnavailable: 1,
            TestContext.Current.CancellationToken);

        Assert.False(recorded);
        Assert.Empty(publisher.Events);
        await using UptimeHarborDbContext verificationContext = CreateContext(connection);
        Assert.Empty(await verificationContext.NotificationOutboxJobs.ToListAsync(
            TestContext.Current.CancellationToken));
    }

    private static async Task<SqliteConnection> OpenDatabaseAsync()
    {
        SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using UptimeHarborDbContext context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static UptimeHarborDbContext CreateContext(SqliteConnection connection)
    {
        return new UptimeHarborDbContext(
            new DbContextOptionsBuilder<UptimeHarborDbContext>()
                .UseSqlite(connection)
                .Options);
    }

    private static async Task<Guid> AddServerAsync(SqliteConnection connection)
    {
        Guid id = Guid.NewGuid();
        await using UptimeHarborDbContext context = CreateContext(connection);
        context.MonitoredServers.Add(
            new MonitoredServer(id, "Server", "https://server.example/health", null, true));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private class ProviderServerRepository(
        UptimeHarborDbContext context,
        Func<Task>? afterFirstTrackedLoad = null)
        : IMonitoredServerRepository
    {
        private bool hookInvoked;

        public async Task<MonitoredServer?> GetItemByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            MonitoredServer? server = await context.MonitoredServers.SingleOrDefaultAsync(
                item => item.Id == id,
                cancellationToken);
            if (!hookInvoked && afterFirstTrackedLoad is not null)
            {
                hookInvoked = true;
                await afterFirstTrackedLoad();
            }

            return server;
        }

        public async Task<IReadOnlyList<MonitoredServer>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return await context.MonitoredServers.AsNoTracking().ToListAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<MonitoredServer>> GetEnabledAsync(CancellationToken cancellationToken = default)
        {
            return await context.MonitoredServers.AsNoTracking()
                .Where(server => server.IsMonitoringEnabled)
                .ToListAsync(cancellationToken);
        }

        public void Create(MonitoredServer item)
        {
            context.MonitoredServers.Add(item);
        }
        public void CreateRange(IEnumerable<MonitoredServer> entities)
        {
            context.MonitoredServers.AddRange(entities);
        }
        public void Delete(MonitoredServer item)
        {
            context.MonitoredServers.Remove(item);
        }
        public void DeleteRange(IEnumerable<MonitoredServer> entities)
        {
            context.MonitoredServers.RemoveRange(entities);
        }
    }

    private class EmptyContainerRepository : IMonitoredContainerRepository
    {
        public Task<IReadOnlyList<MonitoredServer>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<MonitoredServer>>([]);
        }
        public Task<IReadOnlyList<MonitoredServer>> GetEnabledAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<MonitoredServer>>([]);
        }
        public Task<IReadOnlyList<MonitoredServer>> GetByServerIdAsync(Guid serverId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<MonitoredServer>>([]);
        }
        public Task<MonitoredServer?> GetItemByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<MonitoredServer?>(null);
        }
        public void Create(MonitoredServer item)
        {
            throw new NotSupportedException();
        }
        public void CreateRange(IEnumerable<MonitoredServer> entities)
        {
            throw new NotSupportedException();
        }
        public void Delete(MonitoredServer item)
        {
            throw new NotSupportedException();
        }
        public void DeleteRange(IEnumerable<MonitoredServer> entities)
        {
            throw new NotSupportedException();
        }
    }

    private class ProviderMonitoringUnitOfWork(
        UptimeHarborDbContext context,
        IMonitoredServerRepository servers)
        : IMonitoringUnitOfWork
    {
        public IMonitoredServerRepository MonitoredServers { get; } = servers;
        public IMonitoredContainerRepository MonitoredContainers { get; } = new EmptyContainerRepository();

        public void EnqueueNotification(MonitoringStateChangedEvent monitoringEvent)
        {
            context.NotificationOutboxJobs.Add(NotificationOutboxJob.Create(monitoringEvent));
        }

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException exception)
            {
                context.ChangeTracker.Clear();
                throw new PersistenceConcurrencyException(exception);
            }
        }

        public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
        {
            await action(cancellationToken);
            await SaveChangesAsync(cancellationToken);
        }
    }

    private class RecordingMonitoringPublisher : IMonitoringEventPublisher
    {
        public List<MonitoringStateChangedEvent> Events { get; } = [];
        public Task PublishAsync(MonitoringStateChangedEvent monitoringEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(monitoringEvent);
            return Task.CompletedTask;
        }
    }

}
