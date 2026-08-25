using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Application.Visibility;
using UptimeHarbor.Application.Visibility.Models;
using UptimeHarbor.Domain.Access;
using UptimeHarbor.Domain.Monitoring;
using Xunit;

namespace UptimeHarbor.Tests.Application.Visibility;

public class AccessibleObjectsServiceTests
{
    [Fact]
    public async Task GetAllAsync_ForAdministrator_ReturnsAllObjects()
    {
        TestData data = new();
        AccessibleObjectsService service = data.CreateService(isAdministrator: true);

        ServiceResult<IReadOnlyList<AccessibleServerDto>> result =
            await service.GetAllAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.Count);
        Assert.Equal(2, result.Data.SelectMany(server => server.Containers).Count());
    }

    [Fact]
    public async Task GetAllAsync_ForUser_ReturnsOnlyAssignedServerAndContainer()
    {
        TestData data = new();
        data.Permissions.ServerPermissions.Add(
            new ServerVisibilityPermission(data.UserId, data.FirstServer.Id));
        data.Permissions.ContainerPermissions.Add(
            new ContainerVisibilityPermission(
                data.UserId,
                data.FirstServer.Id,
                data.FirstContainer.Id));
        AccessibleObjectsService service = data.CreateService(isAdministrator: false);

        ServiceResult<IReadOnlyList<AccessibleServerDto>> result =
            await service.GetAllAsync(TestContext.Current.CancellationToken);

        AccessibleServerDto server = Assert.Single(result.Data!);
        Assert.Equal(data.FirstServer.Id, server.Id);
        Assert.Equal(data.FirstContainer.Id, Assert.Single(server.Containers).Id);
    }

    [Fact]
    public async Task GetAllAsync_ServerPermissionDoesNotGrantAllContainers()
    {
        TestData data = new();
        data.Permissions.ServerPermissions.Add(
            new ServerVisibilityPermission(data.UserId, data.FirstServer.Id));
        AccessibleObjectsService service = data.CreateService(isAdministrator: false);

        ServiceResult<IReadOnlyList<AccessibleServerDto>> result =
            await service.GetAllAsync(TestContext.Current.CancellationToken);

        Assert.Empty(Assert.Single(result.Data!).Containers);
    }

    [Fact]
    public async Task GetContainerAsync_WhenObjectIsHidden_ReturnsNotFound()
    {
        TestData data = new();
        AccessibleObjectsService service = data.CreateService(isAdministrator: false);

        ServiceResult<AccessibleContainerDto> result =
            await service.GetContainerAsync(data.FirstContainer.Id, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ServiceErrorType.NotFound, result.Error!.Type);
    }

    [Fact]
    public async Task GetAllAsync_WhenUserIsBlocked_ReturnsForbidden()
    {
        TestData data = new();
        AccessibleObjectsService service = data.CreateService(isAdministrator: false, isEnabled: false);

        ServiceResult<IReadOnlyList<AccessibleServerDto>> result =
            await service.GetAllAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ServiceErrorType.Forbidden, result.Error!.Type);
    }

    private class TestData
    {
        public TestData()
        {
            FirstServer = new MonitoredServer(Guid.NewGuid(), "https://server-1/health", null, true);
            MonitoredServer secondServer = new(Guid.NewGuid(), "https://server-2/health", null, true);
            FirstContainer = new MonitoredContainer(
                Guid.NewGuid(),
                FirstServer.Id,
                "https://container-1/health",
                null,
                true);
            MonitoredContainer secondContainer = new(
                Guid.NewGuid(),
                secondServer.Id,
                "https://container-2/health",
                null,
                true);
            Servers = new FakeServerRepository([FirstServer, secondServer]);
            Containers = new FakeContainerRepository([FirstContainer, secondContainer]);
            Permissions = new FakePermissionRepository();
        }

        public Guid UserId { get; } = Guid.NewGuid();

        public MonitoredServer FirstServer { get; }

        public MonitoredContainer FirstContainer { get; }

        public FakeServerRepository Servers { get; }

        public FakeContainerRepository Containers { get; }

        public FakePermissionRepository Permissions { get; }

        public AccessibleObjectsService CreateService(bool isAdministrator, bool isEnabled = true)
        {
            FakeAccessContext context = new(UserId, true, isAdministrator, isEnabled);
            return new AccessibleObjectsService(
                context,
                new FakeVisibilityUnitOfWork(Servers, Containers, Permissions));
        }
    }

    private class FakeAccessContext(
        Guid userId,
        bool isAuthenticated,
        bool isAdministrator,
        bool isEnabled)
        : IUserAccessContext
    {
        public bool IsAuthenticated { get; } = isAuthenticated;
        public bool IsAdministrator { get; } = isAdministrator;
        public bool IsEnabled { get; } = isEnabled;
        public Guid UserId { get; } = userId;
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
        public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default) =>
            await action(cancellationToken);
    }

    private class FakeServerRepository(List<MonitoredServer> items) : IMonitoredServerRepository
    {
        public Task<List<MonitoredServer>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(items.ToList());
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
        public Task<List<MonitoredContainer>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(items.ToList());
        public Task<List<MonitoredContainer>> GetByServerIdAsync(Guid serverId, CancellationToken cancellationToken = default) =>
            Task.FromResult(items.Where(item => item.ServerId == serverId).ToList());
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
        public Task<List<ServerVisibilityPermission>> GetServerPermissionsAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ServerPermissions.Where(item => item.UserId == userId).ToList());
        public Task<List<ContainerVisibilityPermission>> GetContainerPermissionsAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ContainerPermissions.Where(item => item.UserId == userId).ToList());
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
