using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Dashboard;
using UptimeHarbor.Application.Dashboard.Models;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Access;
using UptimeHarbor.Domain.Monitoring;
using Xunit;

namespace UptimeHarbor.Tests.Application.Dashboard;

public class DashboardQueryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnassignedContainer_IsReturnedSeparatelyOnlyForAdministrator(bool isAdministrator)
    {
        // Arrange
        TestData data = new();
        data.AllowServer(data.FirstServer);
        data.AllowContainer(data.FirstContainer);
        data.FirstContainer.AssignServer(null);
        DashboardQuery query = data.CreateQuery(isAdministrator);

        // Act
        ServiceResult<DashboardSnapshotDto> result = await query.GetSnapshotAsync(
            null, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.Success);
        if (isAdministrator)
        {
            Assert.Equal(data.FirstContainer.Id, Assert.Single(result.Data!.UnassignedContainers).Id);
        }
        else
        {
            Assert.Empty(result.Data!.UnassignedContainers);
        }
    }

    private static readonly DateTimeOffset NOW_UTC =
        new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetSnapshotAsync_ForAdministrator_ReturnsAllServersAndContainers()
    {
        TestData data = new();

        ServiceResult<DashboardSnapshotDto> result = await data
            .CreateQuery(isAdministrator: true)
            .GetSnapshotAsync(null, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.Servers.Count);
        Assert.Equal(3, result.Data.Servers.SelectMany(server => server.Containers).Count());
        DashboardServerDto firstServer = Assert.Single(
            result.Data.Servers,
            server => server.Id == data.FirstServer.Id);
        Assert.Equal("Primary server", firstServer.Name);
        Assert.Equal("Primary", firstServer.InternalName);
    }

    [Fact]
    public async Task GetSnapshotAsync_ForUser_ReturnsOnlyPermittedServerAndContainer()
    {
        TestData data = new();
        data.AllowServer(data.FirstServer);
        data.AllowContainer(data.FirstContainer);

        DashboardSnapshotDto snapshot = (await data
            .CreateQuery(isAdministrator: false)
            .GetSnapshotAsync(null, TestContext.Current.CancellationToken)).Data!;

        DashboardServerDto server = Assert.Single(snapshot.Servers);
        Assert.Equal(data.FirstServer.Id, server.Id);
        Assert.Equal(data.FirstContainer.Id, Assert.Single(server.Containers).Id);
    }

    [Fact]
    public async Task GetSnapshotAsync_ServerPermissionDoesNotGrantContainers()
    {
        TestData data = new();
        data.AllowServer(data.FirstServer);

        DashboardSnapshotDto snapshot = (await data
            .CreateQuery(isAdministrator: false)
            .GetSnapshotAsync(null, TestContext.Current.CancellationToken)).Data!;

        Assert.Empty(Assert.Single(snapshot.Servers).Containers);
    }

    [Fact]
    public async Task GetSnapshotAsync_HiddenContainerIsAbsent()
    {
        TestData data = new();
        data.AllowServer(data.FirstServer);
        data.AllowContainer(data.FirstContainer);

        DashboardSnapshotDto snapshot = (await data
            .CreateQuery(isAdministrator: false)
            .GetSnapshotAsync(null, TestContext.Current.CancellationToken)).Data!;

        Assert.DoesNotContain(
            snapshot.Servers.SelectMany(server => server.Containers),
            container => container.Id == data.SecondContainerOnFirstServer.Id);
    }

    [Theory]
    [InlineData("PRODUCTION", 1)]
    [InlineData("payments API", 1)]
    [InlineData("SECONDARY workload", 1)]
    public async Task GetSnapshotAsync_SearchesNameAndDescriptionIgnoringCase(
        string search,
        int expectedServerCount)
    {
        TestData data = new();

        DashboardSnapshotDto snapshot = (await data
            .CreateQuery(isAdministrator: true)
            .GetSnapshotAsync(search, TestContext.Current.CancellationToken)).Data!;

        Assert.Equal(expectedServerCount, snapshot.Servers.Count);
    }

    [Fact]
    public async Task GetSnapshotAsync_SearchDoesNotReturnHiddenObject()
    {
        TestData data = new();
        data.AllowServer(data.FirstServer);

        DashboardSnapshotDto snapshot = (await data
            .CreateQuery(isAdministrator: false)
            .GetSnapshotAsync("secondary", TestContext.Current.CancellationToken)).Data!;

        Assert.Empty(snapshot.Servers);
    }

    [Fact]
    public async Task GetSnapshotAsync_EmptySearchReturnsAccessibleSnapshot()
    {
        TestData data = new();
        data.AllowServer(data.FirstServer);
        data.AllowContainer(data.FirstContainer);
        DashboardQuery query = data.CreateQuery(isAdministrator: false);

        DashboardSnapshotDto withoutSearch = (await query.GetSnapshotAsync(
            null,
            TestContext.Current.CancellationToken)).Data!;
        DashboardSnapshotDto withEmptySearch = (await query.GetSnapshotAsync(
            "   ",
            TestContext.Current.CancellationToken)).Data!;

        Assert.Equal(
            withoutSearch.Servers.Select(server => server.Id),
            withEmptySearch.Servers.Select(server => server.Id));
        Assert.Equal(
            withoutSearch.Servers.SelectMany(server => server.Containers).Select(container => container.Id),
            withEmptySearch.Servers.SelectMany(server => server.Containers).Select(container => container.Id));
    }

    [Fact]
    public async Task GetSnapshotAsync_ServerUsesConfiguredNameAndOmitsMissingInternalName()
    {
        TestData data = new();
        data.AllowServer(data.SecondServer);

        DashboardServerDto server = Assert.Single((await data
            .CreateQuery(isAdministrator: false)
            .GetSnapshotAsync(null, TestContext.Current.CancellationToken)).Data!.Servers);

        Assert.Equal(data.SecondServer.Name, server.Name);
        Assert.Null(server.InternalName);
    }

    [Fact]
    public async Task GetSnapshotAsync_PreservesUtcLastCheckAndUsesTimeProviderForUptime()
    {
        TestData data = new();
        data.AllowServer(data.FirstServer);
        data.AllowContainer(data.FirstContainer);

        DashboardServerDto server = Assert.Single((await data
            .CreateQuery(isAdministrator: false)
            .GetSnapshotAsync(null, TestContext.Current.CancellationToken)).Data!.Servers);
        DashboardContainerDto container = Assert.Single(server.Containers);

        Assert.Equal(NOW_UTC.AddMinutes(-5), server.LastCheckedAtUtc);
        Assert.Equal(TimeSpan.Zero, server.LastCheckedAtUtc!.Value.Offset);
        Assert.Equal(NOW_UTC.AddHours(-2), container.StartedAtUtc);
        Assert.Equal(7_200, container.UptimeSeconds);
    }

    [Fact]
    public async Task GetSnapshotAsync_MissingStartedAtDoesNotCreateUptime()
    {
        TestData data = new();
        data.AllowServer(data.FirstServer);
        data.AllowContainer(data.SecondContainerOnFirstServer);

        DashboardContainerDto container = Assert.Single((await data
            .CreateQuery(isAdministrator: false)
            .GetSnapshotAsync(null, TestContext.Current.CancellationToken)).Data!
            .Servers.Single().Containers);

        Assert.Null(container.StartedAtUtc);
        Assert.Null(container.UptimeSeconds);
    }

    private class TestData
    {
        public TestData()
        {
            FirstServer = new MonitoredServer(
                Guid.NewGuid(),
                "Primary server",
                "https://primary.example/health",
                "Production server",
                true);
            FirstServer.RecordSuccessfulCheck("Primary", NOW_UTC.AddMinutes(-5));
            SecondServer = new MonitoredServer(
                Guid.NewGuid(),
                "Secondary server",
                "https://secondary.example/health",
                "Secondary workload",
                true);
            FirstContainer = new MonitoredContainer(
                Guid.NewGuid(),
                FirstServer.Id,
                "https://payments.example/health",
                "Public API",
                true);
            FirstContainer.RecordSuccessfulCheck(
                "Payments API",
                NOW_UTC.AddHours(-2),
                NOW_UTC.AddMinutes(-1));
            SecondContainerOnFirstServer = new MonitoredContainer(
                Guid.NewGuid(),
                FirstServer.Id,
                "https://worker.example/health",
                "Background worker",
                true);
            MonitoredContainer secondServerContainer = new(
                Guid.NewGuid(),
                SecondServer.Id,
                "https://secondary-app.example/health",
                null,
                true);

            Servers = new FakeServerRepository([FirstServer, SecondServer]);
            Containers = new FakeContainerRepository(
                [FirstContainer, SecondContainerOnFirstServer, secondServerContainer]);
        }

        public Guid UserId { get; } = Guid.NewGuid();
        public MonitoredServer FirstServer { get; }
        public MonitoredServer SecondServer { get; }
        public MonitoredContainer FirstContainer { get; }
        public MonitoredContainer SecondContainerOnFirstServer { get; }
        public FakeServerRepository Servers { get; }
        public FakeContainerRepository Containers { get; }
        public FakePermissionRepository Permissions { get; } = new();

        public void AllowServer(MonitoredServer server) =>
            Permissions.ServerPermissions.Add(new ServerVisibilityPermission(UserId, server.Id));

        public void AllowContainer(MonitoredContainer container)
        {
            Guid serverId = container.ServerId
                ?? throw new InvalidOperationException("Тестовые права требуют сервер.");
            Permissions.ContainerPermissions.Add(new ContainerVisibilityPermission(
                UserId,
                serverId,
                container.Id));
        }

        public DashboardQuery CreateQuery(bool isAdministrator)
        {
            return new DashboardQuery(
                new FakeAccessContext(UserId, isAdministrator),
                new FakeDashboardSnapshotReadQuery(Servers, Containers, Permissions),
                new FixedTimeProvider(NOW_UTC));
        }
    }

    private class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private class FakeAccessContext(Guid userId, bool isAdministrator) : IUserAccessContext
    {
        public bool IsAuthenticated => true;
        public bool IsAdministrator { get; } = isAdministrator;
        public bool IsEnabled => true;
        public Guid UserId { get; } = userId;
    }

    private class FakeDashboardSnapshotReadQuery(
        FakeServerRepository servers,
        FakeContainerRepository containers,
        FakePermissionRepository permissions)
        : IDashboardSnapshotReadQuery
    {
        public Task<DashboardReadSnapshot> GetSnapshotAsync(
            Guid userId,
            bool isAdministrator,
            CancellationToken cancellationToken = default)
        {
            DashboardReadSnapshot snapshot = new(
                servers.Items.Select(server => new DashboardServerReadRecord(
                    server.Id,
                    server.Name,
                    server.Description,
                    server.LastKnownName,
                    server.Status,
                    server.LastCheckedAtUtc)).ToList(),
                containers.Items.Select(container => new DashboardContainerReadRecord(
                    container.Id,
                    container.ServerId,
                    container.Description,
                    container.LastKnownName,
                    container.Status,
                    container.LastCheckedAtUtc,
                    container.StartedAtUtc)).ToList(),
                permissions.ServerPermissions
                    .Where(permission => permission.UserId == userId)
                    .Select(permission => permission.ServerId)
                    .ToList(),
                permissions.ContainerPermissions
                    .Where(permission => permission.UserId == userId)
                    .Select(permission => permission.ContainerId)
                    .ToList());
            return Task.FromResult(snapshot);
        }
    }

    private class FakeVisibilityUnitOfWork(
        IMonitoredServerRepository servers,
        IMonitoredContainerRepository containers,
        IVisibilityPermissionRepository permissions)
        : IVisibilitySettingsUnitOfWork
    {
        public IMonitoredServerRepository MonitoredServers { get; } = servers;
        public IMonitoredContainerRepository MonitoredContainers { get; } = containers;
        public IVisibilityPermissionRepository VisibilityPermissions { get; } = permissions;
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default) =>
            action(cancellationToken);
    }

    private class FakeServerRepository(List<MonitoredServer> items) : IMonitoredServerRepository
    {
        public List<MonitoredServer> Items => items;
        public Task<IReadOnlyList<MonitoredServer>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MonitoredServer>>(items.ToList());
        public Task<IReadOnlyList<MonitoredServer>> GetEnabledAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MonitoredServer>>(
                items.Where(item => item.IsMonitoringEnabled).ToList());
        public Task<MonitoredServer?> GetItemByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(items.SingleOrDefault(item => item.Id == id));
        public void Create(MonitoredServer item) => items.Add(item);
        public void CreateRange(IEnumerable<MonitoredServer> entities) => items.AddRange(entities);
        public void Delete(MonitoredServer item) => items.Remove(item);
        public void DeleteRange(IEnumerable<MonitoredServer> entities)
        {
            foreach (MonitoredServer entity in entities.ToList()) items.Remove(entity);
        }
    }

    private class FakeContainerRepository(List<MonitoredContainer> items) : IMonitoredContainerRepository
    {
        public List<MonitoredContainer> Items => items;
        public Task<IReadOnlyList<MonitoredContainer>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MonitoredContainer>>(items.ToList());
        public Task<IReadOnlyList<MonitoredContainer>> GetEnabledAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MonitoredContainer>>(
                items.Where(item => item.IsMonitoringEnabled).ToList());
        public Task<IReadOnlyList<MonitoredContainer>> GetByServerIdAsync(Guid serverId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MonitoredContainer>>(
                items.Where(item => item.ServerId == serverId).ToList());
        public Task<MonitoredContainer?> GetItemByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(items.SingleOrDefault(item => item.Id == id));
        public void Create(MonitoredContainer item) => items.Add(item);
        public void CreateRange(IEnumerable<MonitoredContainer> entities) => items.AddRange(entities);
        public void Delete(MonitoredContainer item) => items.Remove(item);
        public void DeleteRange(IEnumerable<MonitoredContainer> entities)
        {
            foreach (MonitoredContainer entity in entities.ToList()) items.Remove(entity);
        }
    }

    private class FakePermissionRepository : IVisibilityPermissionRepository
    {
        public List<ServerVisibilityPermission> ServerPermissions { get; } = [];
        public List<ContainerVisibilityPermission> ContainerPermissions { get; } = [];
        public Task<IReadOnlyList<ServerVisibilityPermission>> GetServerPermissionsAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ServerVisibilityPermission>>(
                ServerPermissions.Where(item => item.UserId == userId).ToList());
        public Task<IReadOnlyList<ContainerVisibilityPermission>> GetContainerPermissionsAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ContainerVisibilityPermission>>(
                ContainerPermissions.Where(item => item.UserId == userId).ToList());
        public Task<IReadOnlyList<ServerVisibilityPermission>> GetServerPermissionsForUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ServerVisibilityPermission>>(
                ServerPermissions.Where(item => userIds.Contains(item.UserId)).ToList());
        public Task<IReadOnlyList<ContainerVisibilityPermission>> GetContainerPermissionsForUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ContainerVisibilityPermission>>(
                ContainerPermissions.Where(item => userIds.Contains(item.UserId)).ToList());
        public void Create(ServerVisibilityPermission item) => ServerPermissions.Add(item);
        public void CreateRange(IEnumerable<ServerVisibilityPermission> entities) => ServerPermissions.AddRange(entities);
        public void Create(ContainerVisibilityPermission item) => ContainerPermissions.Add(item);
        public void CreateRange(IEnumerable<ContainerVisibilityPermission> entities) => ContainerPermissions.AddRange(entities);
        public void Delete(ServerVisibilityPermission item) => ServerPermissions.Remove(item);
        public void DeleteRange(IEnumerable<ServerVisibilityPermission> entities)
        {
            foreach (ServerVisibilityPermission entity in entities.ToList()) ServerPermissions.Remove(entity);
        }
        public void Delete(ContainerVisibilityPermission item) => ContainerPermissions.Remove(item);
        public void DeleteRange(IEnumerable<ContainerVisibilityPermission> entities)
        {
            foreach (ContainerVisibilityPermission entity in entities.ToList()) ContainerPermissions.Remove(entity);
        }
    }
}
