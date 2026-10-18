using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Users;
using UptimeHarbor.Application.Users.Models;
using UptimeHarbor.Application.Visibility;
using UptimeHarbor.Application.Visibility.Models;
using UptimeHarbor.Domain.Access;
using UptimeHarbor.Domain.Monitoring;
using Xunit;

namespace UptimeHarbor.Tests.Application.Visibility;

public class MonitoringEventRecipientResolverTests
{
    [Fact]
    public async Task UnassignedContainer_OnlyEnabledAdministratorReceivesEvent()
    {
        // Arrange
        TestData data = new();
        Guid adminId = data.AddUser(isAdministrator: true);
        data.AddUser(isAdministrator: false);

        // Act
        IReadOnlyList<Guid> recipients = await data.Resolver.GetRecipientUserIdsAsync(
            MonitoringObjectKind.Container, Guid.NewGuid(), null, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(adminId, Assert.Single(recipients));
    }

    [Fact]
    public async Task GetRecipientUserIdsAsync_AdministratorReceivesAnyObject()
    {
        TestData data = new();
        Guid administratorId = data.AddUser(isAdministrator: true);

        IReadOnlyList<Guid> recipients = await data.Resolver.GetRecipientUserIdsAsync(
            MonitoringObjectKind.Container,
            Guid.NewGuid(),
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        Assert.Equal(administratorId, Assert.Single(recipients));
    }

    [Fact]
    public async Task GetRecipientUserIdsAsync_UserDoesNotReceiveHiddenServer()
    {
        TestData data = new();
        data.AddUser(isAdministrator: false);

        IReadOnlyList<Guid> recipients = await data.Resolver.GetRecipientUserIdsAsync(
            MonitoringObjectKind.Server,
            Guid.NewGuid(),
            null,
            TestContext.Current.CancellationToken);

        Assert.Empty(recipients);
    }

    [Fact]
    public async Task GetRecipientUserIdsAsync_UserReceivesOnlyPermittedServer()
    {
        TestData data = new();
        Guid permittedUserId = data.AddUser(isAdministrator: false);
        data.AddUser(isAdministrator: false);
        Guid serverId = Guid.NewGuid();
        data.Permissions.ServerPermissions.Add(
            new ServerVisibilityPermission(permittedUserId, serverId));

        IReadOnlyList<Guid> recipients = await data.Resolver.GetRecipientUserIdsAsync(
            MonitoringObjectKind.Server,
            serverId,
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(permittedUserId, Assert.Single(recipients));
    }

    [Fact]
    public async Task GetRecipientUserIdsAsync_UserDoesNotReceiveHiddenContainer()
    {
        TestData data = new();
        Guid userId = data.AddUser(isAdministrator: false);
        Guid serverId = Guid.NewGuid();
        data.Permissions.ServerPermissions.Add(new ServerVisibilityPermission(userId, serverId));

        IReadOnlyList<Guid> recipients = await data.Resolver.GetRecipientUserIdsAsync(
            MonitoringObjectKind.Container,
            Guid.NewGuid(),
            serverId,
            TestContext.Current.CancellationToken);

        Assert.Empty(recipients);
    }

    [Fact]
    public async Task GetRecipientUserIdsAsync_ContainerRequiresServerAndContainerPermissions()
    {
        TestData data = new();
        Guid userId = data.AddUser(isAdministrator: false);
        Guid serverId = Guid.NewGuid();
        Guid containerId = Guid.NewGuid();
        data.Permissions.ContainerPermissions.Add(
            new ContainerVisibilityPermission(userId, serverId, containerId));

        IReadOnlyList<Guid> withoutServer = await data.Resolver.GetRecipientUserIdsAsync(
            MonitoringObjectKind.Container,
            containerId,
            serverId,
            TestContext.Current.CancellationToken);
        data.Permissions.ServerPermissions.Add(new ServerVisibilityPermission(userId, serverId));
        IReadOnlyList<Guid> withBoth = await data.Resolver.GetRecipientUserIdsAsync(
            MonitoringObjectKind.Container,
            containerId,
            serverId,
            TestContext.Current.CancellationToken);

        Assert.Empty(withoutServer);
        Assert.Equal(userId, Assert.Single(withBoth));
    }

    [Fact]
    public async Task GetRecipientUserIdsAsync_DisabledAdministratorDoesNotReceiveEvent()
    {
        TestData data = new();
        data.Users.Items.Add(new UserAccountRecord(Guid.NewGuid(), "disabled", false, true));

        IReadOnlyList<Guid> recipients = await data.Resolver.GetRecipientUserIdsAsync(
            MonitoringObjectKind.Server,
            Guid.NewGuid(),
            null,
            TestContext.Current.CancellationToken);

        Assert.Empty(recipients);
    }

    [Fact]
    public async Task GetRecipientUserIdsAsync_LoadsPermissionsInBatches()
    {
        TestData data = new();
        Guid serverId = Guid.NewGuid();
        Guid containerId = Guid.NewGuid();
        foreach (int _ in Enumerable.Range(0, 3))
        {
            Guid userId = data.AddUser(isAdministrator: false);
            data.Permissions.ServerPermissions.Add(new ServerVisibilityPermission(userId, serverId));
            data.Permissions.ContainerPermissions.Add(
                new ContainerVisibilityPermission(userId, serverId, containerId));
        }

        IReadOnlyList<Guid> recipients = await data.Resolver.GetRecipientUserIdsAsync(
            MonitoringObjectKind.Container,
            containerId,
            serverId,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, recipients.Count);
        Assert.Equal(1, data.ReadQuery.ReadCount);
    }

    private class TestData
    {
        public TestData()
        {
            ReadQuery = new FakeRecipientReadQuery(Users, Permissions);
            Resolver = new MonitoringEventRecipientResolver(ReadQuery);
        }

        public FakeUserAccountStore Users { get; } = new();
        public FakePermissionRepository Permissions { get; } = new();
        public FakeRecipientReadQuery ReadQuery { get; }
        public MonitoringEventRecipientResolver Resolver { get; }

        public Guid AddUser(bool isAdministrator)
        {
            Guid id = Guid.NewGuid();
            Users.Items.Add(new UserAccountRecord(id, id.ToString(), true, isAdministrator));
            return id;
        }
    }

    private class FakeRecipientReadQuery(
        FakeUserAccountStore users,
        FakePermissionRepository permissions)
        : IMonitoringRecipientReadQuery
    {
        public int ReadCount { get; private set; }

        public Task<IReadOnlyList<MonitoringRecipientReadRecord>> GetCandidatesAsync(
            MonitoringObjectKind objectKind,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            IReadOnlyList<MonitoringRecipientReadRecord> candidates = users.Items
                .Select(user => new MonitoringRecipientReadRecord(
                    user.Id,
                    user.IsEnabled,
                    user.IsAdministrator,
                    permissions.ServerPermissions
                        .Where(permission => permission.UserId == user.Id)
                        .Select(permission => permission.ServerId)
                        .ToList(),
                    objectKind == MonitoringObjectKind.Container
                        ? permissions.ContainerPermissions
                            .Where(permission => permission.UserId == user.Id)
                            .Select(permission => permission.ContainerId)
                            .ToList()
                        : []))
                .ToList();
            return Task.FromResult(candidates);
        }
    }

    private class FakeUserAccountStore : IUserAccountStore
    {
        public List<UserAccountRecord> Items { get; } = [];
        public Task<IReadOnlyList<UserAccountRecord>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UserAccountRecord>>(Items.ToList());
        public Task<UserAccountRecord?> GetByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.SingleOrDefault(user => user.Id == userId));
        public Task<bool> HasOtherEnabledAdministratorAsync(Guid excludedUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.Any(user => user.Id != excludedUserId && user.IsAdministrator && user.IsEnabled));
        public Task<AccountStoreResult> CreateAsync(Guid userId, string login, string initialPassword, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AccountStoreResult> SetEnabledAsync(Guid userId, bool isEnabled, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AccountStoreResult> DeleteAsync(Guid userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private class FakeVisibilityUnitOfWork(IVisibilityPermissionRepository permissions)
        : IVisibilitySettingsUnitOfWork
    {
        public IMonitoredServerRepository MonitoredServers { get; } = new EmptyServerRepository();
        public IMonitoredContainerRepository MonitoredContainers { get; } = new EmptyContainerRepository();
        public IVisibilityPermissionRepository VisibilityPermissions { get; } = permissions;
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default) =>
            action(cancellationToken);
    }

    private class EmptyServerRepository : IMonitoredServerRepository
    {
        public Task<IReadOnlyList<MonitoredServer>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MonitoredServer>>([]);
        public Task<IReadOnlyList<MonitoredServer>> GetEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MonitoredServer>>([]);
        public Task<MonitoredServer?> GetItemByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<MonitoredServer?>(null);
        public void Create(MonitoredServer item) => throw new NotSupportedException();
        public void CreateRange(IEnumerable<MonitoredServer> entities) => throw new NotSupportedException();
        public void Delete(MonitoredServer item) => throw new NotSupportedException();
        public void DeleteRange(IEnumerable<MonitoredServer> entities) => throw new NotSupportedException();
    }

    private class EmptyContainerRepository : IMonitoredContainerRepository
    {
        public Task<IReadOnlyList<MonitoredContainer>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MonitoredContainer>>([]);
        public Task<IReadOnlyList<MonitoredContainer>> GetEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MonitoredContainer>>([]);
        public Task<IReadOnlyList<MonitoredContainer>> GetByServerIdAsync(Guid serverId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MonitoredContainer>>([]);
        public Task<MonitoredContainer?> GetItemByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<MonitoredContainer?>(null);
        public void Create(MonitoredContainer item) => throw new NotSupportedException();
        public void CreateRange(IEnumerable<MonitoredContainer> entities) => throw new NotSupportedException();
        public void Delete(MonitoredContainer item) => throw new NotSupportedException();
        public void DeleteRange(IEnumerable<MonitoredContainer> entities) => throw new NotSupportedException();
    }

    private class FakePermissionRepository : IVisibilityPermissionRepository
    {
        public List<ServerVisibilityPermission> ServerPermissions { get; } = [];
        public List<ContainerVisibilityPermission> ContainerPermissions { get; } = [];
        public int SingleUserServerReadCount { get; private set; }
        public int SingleUserContainerReadCount { get; private set; }
        public int BatchServerReadCount { get; private set; }
        public int BatchContainerReadCount { get; private set; }
        public Task<IReadOnlyList<ServerVisibilityPermission>> GetServerPermissionsAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            SingleUserServerReadCount++;
            return Task.FromResult<IReadOnlyList<ServerVisibilityPermission>>(
                ServerPermissions.Where(item => item.UserId == userId).ToList());
        }
        public Task<IReadOnlyList<ContainerVisibilityPermission>> GetContainerPermissionsAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            SingleUserContainerReadCount++;
            return Task.FromResult<IReadOnlyList<ContainerVisibilityPermission>>(
                ContainerPermissions.Where(item => item.UserId == userId).ToList());
        }
        public Task<IReadOnlyList<ServerVisibilityPermission>> GetServerPermissionsForUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
        {
            BatchServerReadCount++;
            return Task.FromResult<IReadOnlyList<ServerVisibilityPermission>>(
                ServerPermissions.Where(item => userIds.Contains(item.UserId)).ToList());
        }
        public Task<IReadOnlyList<ContainerVisibilityPermission>> GetContainerPermissionsForUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
        {
            BatchContainerReadCount++;
            return Task.FromResult<IReadOnlyList<ContainerVisibilityPermission>>(
                ContainerPermissions.Where(item => userIds.Contains(item.UserId)).ToList());
        }
        public void Create(ServerVisibilityPermission item) => ServerPermissions.Add(item);
        public void CreateRange(IEnumerable<ServerVisibilityPermission> entities) => ServerPermissions.AddRange(entities);
        public void Create(ContainerVisibilityPermission item) => ContainerPermissions.Add(item);
        public void CreateRange(IEnumerable<ContainerVisibilityPermission> entities) => ContainerPermissions.AddRange(entities);
        public void Delete(ServerVisibilityPermission item) => ServerPermissions.Remove(item);
        public void DeleteRange(IEnumerable<ServerVisibilityPermission> entities) => ServerPermissions.RemoveAll(entities.Contains);
        public void Delete(ContainerVisibilityPermission item) => ContainerPermissions.Remove(item);
        public void DeleteRange(IEnumerable<ContainerVisibilityPermission> entities) => ContainerPermissions.RemoveAll(entities.Contains);
    }
}
