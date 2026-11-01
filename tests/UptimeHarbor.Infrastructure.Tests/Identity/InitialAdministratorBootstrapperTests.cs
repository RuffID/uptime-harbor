using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Application.Users.Models;
using UptimeHarbor.Infrastructure.Identity;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Identity;

public class InitialAdministratorBootstrapperTests
{
    [Fact]
    public async Task InitializeAsync_WhenStoreIsEmpty_CreatesRolesAndAdministrator()
    {
        // Arrange
        FakeInitialAdministratorStore store = new();
        InitialAdministratorBootstrapper bootstrapper = CreateBootstrapper(store);

        // Act
        await bootstrapper.InitializeAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([ApplicationRoles.ADMINISTRATOR], store.EnsuredRoles);
        Assert.Equal("admin", store.CreatedLogin);
        Assert.Equal("initial-password", store.CreatedPassword);
    }

    [Fact]
    public async Task InitializeAsync_WhenRepeated_DoesNotChangeAdministratorPassword()
    {
        FakeInitialAdministratorStore store = new();
        InitialAdministratorBootstrapper bootstrapper = CreateBootstrapper(store);

        await bootstrapper.InitializeAsync(TestContext.Current.CancellationToken);
        await bootstrapper.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, store.CreateCount);
        Assert.Equal("initial-password", store.CreatedPassword);
    }

    [Fact]
    public async Task InitializeAsync_WhenOnlyAdministratorIsDisabled_ReenablesWithoutDuplicate()
    {
        FakeInitialAdministratorStore store = new(hasAdministrator: true, administratorEnabled: false);
        InitialAdministratorBootstrapper bootstrapper = CreateBootstrapper(store);

        await bootstrapper.InitializeAsync(TestContext.Current.CancellationToken);
        await bootstrapper.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, store.CreateCount);
        Assert.Equal(1, store.EnableCount);
        Assert.True(store.AdministratorEnabled);
    }

    [Fact]
    public async Task InitializeAsync_WhenEnabledAdministratorIsIdentityLockedOut_DoesNotCreateOrEnableAnother()
    {
        FakeInitialAdministratorStore store = new(
            hasAdministrator: true,
            administratorEnabled: true);
        InitialAdministratorBootstrapper bootstrapper = CreateBootstrapper(store);

        await bootstrapper.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, store.CreateCount);
        Assert.Equal(0, store.EnableCount);
        Assert.True(store.AdministratorEnabled);
    }

    private static InitialAdministratorBootstrapper CreateBootstrapper(
        FakeInitialAdministratorStore store)
    {
        return new InitialAdministratorBootstrapper(
            store,
            Options.Create(new BootstrapAdminOptions
            {
                Login = "admin",
                Password = "initial-password"
            }));
    }

    private class FakeInitialAdministratorStore(
        bool hasAdministrator = false,
        bool administratorEnabled = true)
        : IInitialAdministratorStore
    {
        private readonly Guid administratorId = Guid.NewGuid();
        public List<string> EnsuredRoles { get; } = [];

        public int CreateCount { get; private set; }

        public string? CreatedLogin { get; private set; }

        public string? CreatedPassword { get; private set; }

        public int EnableCount { get; private set; }

        public bool AdministratorEnabled { get; private set; } = administratorEnabled;

        public Task<IReadOnlyList<UserAccountRecord>> GetAdministratorsAsync(
            CancellationToken cancellationToken = default)
        {
            bool exists = hasAdministrator || CreateCount > 0;
            IReadOnlyList<UserAccountRecord> administrators = exists
                ? [new UserAccountRecord(administratorId, "admin", AdministratorEnabled, true)]
                : [];
            return Task.FromResult(administrators);
        }

        public Task<AccountStoreResult> EnsureRoleAsync(
            string roleName,
            CancellationToken cancellationToken = default)
        {
            if (!EnsuredRoles.Contains(roleName, StringComparer.Ordinal))
            {
                EnsuredRoles.Add(roleName);
            }

            return Task.FromResult(AccountStoreResult.Success());
        }

        public Task<AccountStoreResult> CreateAdministratorAsync(
            Guid userId,
            string login,
            string password,
            CancellationToken cancellationToken = default)
        {
            CreateCount++;
            AdministratorEnabled = true;
            CreatedLogin = login;
            CreatedPassword = password;
            return Task.FromResult(AccountStoreResult.Success());
        }

        public Task<AccountStoreResult> EnableAdministratorAsync(
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(administratorId, userId);
            EnableCount++;
            AdministratorEnabled = true;
            return Task.FromResult(AccountStoreResult.Success());
        }
    }
}
