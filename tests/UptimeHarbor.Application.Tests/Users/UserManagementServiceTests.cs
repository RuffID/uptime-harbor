using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Application.Users;
using UptimeHarbor.Application.Users.Models;
using UptimeHarbor.Domain.Access;
using UptimeHarbor.Domain.Monitoring;
using Xunit;

namespace UptimeHarbor.Tests.Application.Users;

public class UserManagementServiceTests
{
    [Fact]
    public async Task CreateUserAsync_ForOrdinaryUser_ReturnsForbidden()
    {
        TestData data = new(isAdministrator: false);

        ServiceResult<UserAccountDto> result =
            await data.Service.CreateUserAsync(
                "new-user",
                "initial-password",
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ServiceErrorType.Forbidden, result.Error!.Type);
        Assert.Equal(0, data.Accounts.CreateCount);
    }

    [Fact]
    public async Task SetVisibilityAsync_WhenContainerParentIsNotAssigned_ReturnsValidationError()
    {
        TestData data = new(isAdministrator: true);

        ServiceResult result = await data.Service.SetVisibilityAsync(
            data.ManagedUserId,
            [],
            [data.Container.Id],
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ServiceErrorType.Validation, result.Error!.Type);
        Assert.Empty(data.Permissions.ContainerPermissions);
    }

    [Fact]
    public async Task DeleteAsync_RemovesUserAndPermissions()
    {
        TestData data = new(isAdministrator: true);
        data.Permissions.ServerPermissions.Add(
            new ServerVisibilityPermission(data.ManagedUserId, data.Server.Id));
        data.Permissions.ContainerPermissions.Add(
            new ContainerVisibilityPermission(
                data.ManagedUserId,
                data.Server.Id,
                data.Container.Id));

        ServiceResult result = await data.Service.DeleteAsync(
            data.ManagedUserId,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Null(await data.Accounts.GetByIdAsync(
            data.ManagedUserId,
            TestContext.Current.CancellationToken));
        Assert.Empty(data.Permissions.ServerPermissions);
        Assert.Empty(data.Permissions.ContainerPermissions);
    }

    [Fact]
    public async Task SetEnabledAsync_CannotDisableCurrentAdministrator()
    {
        TestData data = new(isAdministrator: true);

        ServiceResult result = await data.Service.SetEnabledAsync(
            data.AdministratorId,
            false,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ServiceErrorType.Forbidden, result.Error!.Type);
        Assert.Equal(0, data.Accounts.SetEnabledCount);
    }

    [Fact]
    public async Task DeleteAsync_CannotDeleteCurrentAdministrator()
    {
        TestData data = new(isAdministrator: true);

        ServiceResult result = await data.Service.DeleteAsync(
            data.AdministratorId,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ServiceErrorType.Forbidden, result.Error!.Type);
        Assert.NotNull(await data.Accounts.GetByIdAsync(
            data.ManagedUserId,
            TestContext.Current.CancellationToken));
    }

    private class TestData
    {
        public TestData(bool isAdministrator)
        {
            Server = new MonitoredServer(Guid.NewGuid(), "https://server/health", null, true);
            Container = new MonitoredContainer(
                Guid.NewGuid(),
                Server.Id,
                "https://container/health",
                null,
                true);
            Accounts = new FakeAccountStore(
                new UserAccountRecord(ManagedUserId, "managed-user", true, false));
            Permissions = new FakePermissionRepository();
            FakeVisibilityUnitOfWork unitOfWork = new(
                new FakeServerRepository([Server]),
                new FakeContainerRepository([Container]),
                Permissions);
            Service = new UserManagementService(
                new FakeAccessContext(AdministratorId, true, isAdministrator, true),
                Accounts,
                unitOfWork);
        }

        public Guid ManagedUserId { get; } = Guid.NewGuid();
        public Guid AdministratorId { get; } = Guid.NewGuid();
        public MonitoredServer Server { get; }
        public MonitoredContainer Container { get; }
        public FakeAccountStore Accounts { get; }
        public FakePermissionRepository Permissions { get; }
        public UserManagementService Service { get; }
    }

    private class FakeAccessContext(Guid userId, bool authenticated, bool administrator, bool enabled)
        : IUserAccessContext
    {
        public bool IsAuthenticated { get; } = authenticated;
        public bool IsAdministrator { get; } = administrator;
        public bool IsEnabled { get; } = enabled;
        public Guid UserId { get; } = userId;
    }

    private class FakeAccountStore(UserAccountRecord initialUser) : IUserAccountStore
    {
        private readonly List<UserAccountRecord> users = [initialUser];
        public int CreateCount { get; private set; }
        public int SetEnabledCount { get; private set; }
        public Task<IReadOnlyList<UserAccountRecord>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UserAccountRecord>>(users.ToList());
        public Task<UserAccountRecord?> GetByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(users.SingleOrDefault(user => user.Id == userId));
        public Task<AccountStoreResult> CreateAsync(Guid userId, string login, string initialPassword, CancellationToken cancellationToken = default)
        {
            CreateCount++;
            users.Add(new UserAccountRecord(userId, login, true, false));
            return Task.FromResult(AccountStoreResult.Success());
        }
        public Task<AccountStoreResult> SetEnabledAsync(Guid userId, bool isEnabled, CancellationToken cancellationToken = default)
        {
            SetEnabledCount++;
            return Task.FromResult(AccountStoreResult.Success());
        }
        public Task<AccountStoreResult> DeleteAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            users.RemoveAll(user => user.Id == userId);
            return Task.FromResult(AccountStoreResult.Success());
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
        public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
        {
            await action(cancellationToken);
            await SaveChangesAsync(cancellationToken);
        }
    }

    private class FakeServerRepository(List<MonitoredServer> items) : IMonitoredServerRepository
    {
        public Task<List<MonitoredServer>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(items.ToList());
        public Task<List<MonitoredServer>> GetEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult(items.Where(item => item.IsMonitoringEnabled).ToList());
        public Task<MonitoredServer?> GetItemByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(items.SingleOrDefault(item => item.Id == id));
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
        public Task<List<MonitoredContainer>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(items.ToList());
        public Task<List<MonitoredContainer>> GetEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult(items.Where(item => item.IsMonitoringEnabled).ToList());
        public Task<List<MonitoredContainer>> GetByServerIdAsync(Guid serverId, CancellationToken cancellationToken = default) => Task.FromResult(items.Where(item => item.ServerId == serverId).ToList());
        public Task<MonitoredContainer?> GetItemByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(items.SingleOrDefault(item => item.Id == id));
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
        public Task<List<ServerVisibilityPermission>> GetServerPermissionsAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(ServerPermissions.Where(item => item.UserId == userId).ToList());
        public Task<List<ContainerVisibilityPermission>> GetContainerPermissionsAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(ContainerPermissions.Where(item => item.UserId == userId).ToList());
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
