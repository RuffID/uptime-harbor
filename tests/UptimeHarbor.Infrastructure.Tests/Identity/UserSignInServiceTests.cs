using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Infrastructure.Persistence;
using UptimeHarbor.Application.Security;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Identity;

public class UserSignInServiceTests
{
    private const string VALID_PASSWORD = "Valid1!Password";
    private const string INVALID_PASSWORD = "Wrong1!Password";

    [Fact]
    public async Task PasswordSignInAsync_FifthFailureTemporarilyLocksUser()
    {
        await using IdentityTestContext context = await IdentityTestContext.CreateAsync();
        ApplicationUser user = await context.CreateUserAsync("user");

        foreach (int _ in Enumerable.Range(0, 5))
        {
            bool succeeded = await context.SignInService.PasswordSignInAsync(
                "user",
                INVALID_PASSWORD,
                false,
                TestContext.Current.CancellationToken);
            Assert.False(succeeded);
        }

        ApplicationUser storedUser = await context.UserManager.FindByIdAsync(user.Id.ToString())
            ?? throw new InvalidOperationException("Пользователь не найден после входа.");
        DateTimeOffset? lockoutEnd = await context.UserManager.GetLockoutEndDateAsync(storedUser);
        Assert.True(await context.UserManager.IsLockedOutAsync(storedUser));
        Assert.NotNull(lockoutEnd);
        Assert.InRange(
            lockoutEnd!.Value,
            DateTimeOffset.UtcNow.AddMinutes(4),
            DateTimeOffset.UtcNow.AddMinutes(6));
    }

    [Fact]
    public async Task PasswordSignInAsync_SuccessBeforeThresholdResetsFailureCount()
    {
        await using IdentityTestContext context = await IdentityTestContext.CreateAsync();
        ApplicationUser user = await context.CreateUserAsync("user");
        await context.SignInService.PasswordSignInAsync(
            "user",
            INVALID_PASSWORD,
            false,
            TestContext.Current.CancellationToken);
        await context.SignInService.PasswordSignInAsync(
            "user",
            INVALID_PASSWORD,
            false,
            TestContext.Current.CancellationToken);

        bool succeeded = await context.SignInService.PasswordSignInAsync(
            "user",
            VALID_PASSWORD,
            false,
            TestContext.Current.CancellationToken);

        ApplicationUser storedUser = await context.UserManager.FindByIdAsync(user.Id.ToString())
            ?? throw new InvalidOperationException("Пользователь не найден после входа.");
        Assert.True(succeeded);
        Assert.Equal(0, await context.UserManager.GetAccessFailedCountAsync(storedUser));
        Assert.False(await context.UserManager.IsLockedOutAsync(storedUser));
    }

    [Fact]
    public async Task PasswordSignInAsync_DisabledUserDoesNotIncrementIdentityLockout()
    {
        await using IdentityTestContext context = await IdentityTestContext.CreateAsync();
        ApplicationUser user = await context.CreateUserAsync("administrator");
        IdentityResult roleResult = await context.RoleManager.CreateAsync(
            new ApplicationRole(Guid.NewGuid(), ApplicationRoles.ADMINISTRATOR));
        Assert.True(roleResult.Succeeded);
        IdentityResult addToRoleResult = await context.UserManager.AddToRoleAsync(
            user,
            ApplicationRoles.ADMINISTRATOR);
        Assert.True(addToRoleResult.Succeeded);
        user.Disable();
        IdentityResult updateResult = await context.UserManager.UpdateAsync(user);
        Assert.True(updateResult.Succeeded);

        bool succeeded = await context.SignInService.PasswordSignInAsync(
            "administrator",
            INVALID_PASSWORD,
            false,
            TestContext.Current.CancellationToken);

        ApplicationUser storedUser = await context.UserManager.FindByIdAsync(user.Id.ToString())
            ?? throw new InvalidOperationException("Пользователь не найден после входа.");
        Assert.False(succeeded);
        Assert.False(storedUser.IsEnabled);
        Assert.Equal(0, await context.UserManager.GetAccessFailedCountAsync(storedUser));
        Assert.False(await context.UserManager.IsLockedOutAsync(storedUser));
    }

    private class IdentityTestContext : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ServiceProvider provider;
        private readonly AsyncServiceScope scope;

        private IdentityTestContext(
            SqliteConnection connection,
            ServiceProvider provider,
            AsyncServiceScope scope)
        {
            this.connection = connection;
            this.provider = provider;
            this.scope = scope;
            UserManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            RoleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            SignInManager<ApplicationUser> signInManager =
                scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
            signInManager.Context = new DefaultHttpContext
            {
                RequestServices = scope.ServiceProvider
            };
            SignInService = new UserSignInService(UserManager, signInManager);
        }

        public UserManager<ApplicationUser> UserManager { get; }

        public RoleManager<ApplicationRole> RoleManager { get; }

        public UserSignInService SignInService { get; }

        public static async Task<IdentityTestContext> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            ServiceCollection services = new();
            services.AddLogging();
            services.AddHttpContextAccessor();
            services.AddDbContext<UptimeHarborDbContext>(options => options.UseSqlite(connection));
            services
                .AddIdentity<ApplicationUser, ApplicationRole>(options =>
                {
                    options.Lockout.AllowedForNewUsers = true;
                    options.Lockout.MaxFailedAccessAttempts = 5;
                    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
                })
                .AddEntityFrameworkStores<UptimeHarborDbContext>();
            ServiceProvider provider = services.BuildServiceProvider();
            AsyncServiceScope scope = provider.CreateAsyncScope();
            UptimeHarborDbContext dbContext =
                scope.ServiceProvider.GetRequiredService<UptimeHarborDbContext>();
            await dbContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            IHttpContextAccessor accessor =
                scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
            accessor.HttpContext = new DefaultHttpContext
            {
                RequestServices = scope.ServiceProvider
            };
            return new IdentityTestContext(connection, provider, scope);
        }

        public async Task<ApplicationUser> CreateUserAsync(string login)
        {
            ApplicationUser user = new(Guid.NewGuid(), login);
            IdentityResult result = await UserManager.CreateAsync(user, VALID_PASSWORD);
            Assert.True(result.Succeeded, string.Join(" ", result.Errors.Select(error => error.Description)));
            return user;
        }

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await provider.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
