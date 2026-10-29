using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Identity;

namespace UptimeHarbor.Tests.Infrastructure.Identity;

public class AcceptedIdentityFixture : IDisposable
{
    public RevisionStateStore State { get; } = new();
    public AcceptedAccountSession Sessions { get; }
    public AccountCredentialService Credentials { get; }
    public LocalWork Work { get; } = new();
    public AuthenticationService Authentication { get; } = new();
    public Clock Time { get; } = new();
    public AuditLog<AcceptedUserSignInService> Logs { get; } = new();
    public AcceptedUserSignInService SignIn { get; }
    private readonly ServiceProvider services;
    private readonly UserManager<ApplicationUser> users;

    public AcceptedIdentityFixture()
    {
        IdentityOptions identity = new();
        identity.Lockout.MaxFailedAccessAttempts = 5;
        identity.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        PasswordHasher<ApplicationUser> hasher = new(Options.Create(new PasswordHasherOptions { IterationCount = 1000 }));
        users = new UserManager<ApplicationUser>(new Store(), Options.Create(identity), hasher, [],
            [new PasswordValidator<ApplicationUser>()], new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(), new ServiceCollection().BuildServiceProvider(), NullLogger<UserManager<ApplicationUser>>.Instance);
        Credentials = new AccountCredentialService(users, hasher);
        Sessions = new AcceptedAccountSession(State);
        services = new ServiceCollection().AddSingleton<IAuthenticationService>(Authentication).BuildServiceProvider();
        HttpContextAccessor accessor = new() { HttpContext = new DefaultHttpContext { RequestServices = services } };
        LocalAccountLogin local = new(Work, Credentials, Options.Create(identity), Time, new LocalAccountLoginGate());
        SignIn = new AcceptedUserSignInService(State, Sessions, local, accessor, Logs);
    }

    public async Task<SharedAccount> CreateAsync()
    {
        Guid id = Guid.NewGuid();
        var credential = await Credentials.PrepareAsync(id, "admin", "Secret-123!", CancellationToken.None);
        SharedAccount account = new(id, Guid.NewGuid(), "admin", credential.Data!, true, 1);
        Publish(account);
        return account;
    }

    public void Publish(SharedAccount account)
    {
        ConfigurationValue data = new()
        {
            Name = account.Login, RoleId = account.RoleId, Enabled = account.IsEnabled,
            PasswordHash = account.Credential.PasswordHash, PasswordHashFormat = account.Credential.FormatVersion,
            SecurityStamp = account.Credential.SecurityStamp, ConcurrencyStamp = account.Credential.ConcurrencyStamp
        };
        RevisionImage image = State.Current ?? new RevisionImage(Guid.NewGuid(), 1);
        AcceptedValue value = new(account.UserId, DeletedEntityKind.Account, account.UserRevision,
            data, account.DeletedAtUtc.HasValue, account.DeletedAtUtc);
        State.Publish(image with { Index = account.UserRevision, Values = image.Values.SetItem(account.UserId, value) });
        if (Work.User is null)
        {
            Work.User = new ApplicationUser(account.UserId, account.Login);
        }

        Work.User.ApplySharedAccount(account);
    }

    public void Dispose()
    {
        users.Dispose();
        services.Dispose();
    }

    public class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }

    public class LocalWork : ILocalLoginUnitOfWork
    {
        public ApplicationUser? User { get; set; }
        public int Saves { get; private set; }
        public Func<Task>? BeforeSave { get; set; }
        public Task<ApplicationUser?> FindAsync(Guid userId, CancellationToken token)
        {
            return Task.FromResult(User);
        }

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (BeforeSave is not null)
            {
                await BeforeSave();
            }

            Saves++;
            return 1;
        }

        public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    public class AuthenticationService : IAuthenticationService
    {
        public ClaimsPrincipal? Principal { get; private set; }
        public Action? BeforeSignIn { get; set; }
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            BeforeSignIn?.Invoke();
            Principal = principal;
            return Task.CompletedTask;
        }

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            Principal = null;
            return Task.CompletedTask;
        }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            throw new NotSupportedException();
        }

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            throw new NotSupportedException();
        }

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            throw new NotSupportedException();
        }
    }

    public class AuditLog<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    private class Store : IUserStore<ApplicationUser>
    {
        public void Dispose()
        {
        }
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken)
        {
            return Task.FromResult(user.UserName);
        }

        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}
