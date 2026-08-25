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
        FakeInitialAdministratorStore store = new();
        InitialAdministratorBootstrapper bootstrapper = CreateBootstrapper(store);

        await bootstrapper.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal([ApplicationRoles.ADMINISTRATOR, ApplicationRoles.USER], store.EnsuredRoles);
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

    private class FakeInitialAdministratorStore : IInitialAdministratorStore
    {
        public List<string> EnsuredRoles { get; } = [];

        public int CreateCount { get; private set; }

        public string? CreatedLogin { get; private set; }

        public string? CreatedPassword { get; private set; }

        public Task<bool> AdministratorExistsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CreateCount > 0);
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
            CreatedLogin = login;
            CreatedPassword = password;
            return Task.FromResult(AccountStoreResult.Success());
        }
    }
}
