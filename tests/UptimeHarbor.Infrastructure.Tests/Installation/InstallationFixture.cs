using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class InstallationFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "uptime-setup-" + Guid.NewGuid().ToString("N"));
    public InstallationOptions Options { get; }
    public ClusterOptions Cluster { get; }
    public InstallationLease Lease { get; }
    public Clock Time { get; } = new();
    public MemoryStore Store { get; } = new();
    public InstallationState State { get; } = new();
    public Inventory InventoryState { get; } = new();
    public SetupPreflight Preflight { get; }
    public LocalSecretStore Secrets { get; }
    public SetupAccountLogin Login { get; }
    public ISetupBotIdentity BotIdentity => new SetupBotIdentity(Microsoft.Extensions.Options.Options.Create(Cluster),
        Microsoft.Extensions.Options.Options.Create(new UptimeHarbor.Infrastructure.Telegram.TelegramOptions()), new NoNetworkClients());
    public const string ORIGINAL_PASSWORD = "Local-A9!v6Qx7w";

    public InstallationFixture()
    {
        Options = new()
        {
            InstallationIntent = "Setup", StateDirectory = Path.Combine(Root, "state"),
            KeyDirectory = Path.Combine(Root, "keys"), SecretDirectory = Path.Combine(Root, "secret"),
            BrowserOrigin = "https://browser.example", PeerEndpoint = "https://peer.example:8443/internal/raft"
        };
        Cluster = new()
        {
            StoragePath = Path.Combine(Root, "storage"), ProtocolMajor = 2, CommandSchema = 2,
            SnapshotFormat = 2, SqlSchema = 2
        };
        Lease = new(Options, Cluster.StoragePath);
        Preflight = new(InventoryState, Microsoft.Extensions.Options.Options.Create(Options),
            Microsoft.Extensions.Options.Options.Create(Cluster), new(true, Options.PeerEndpoint), Lease);
        Secrets = new(Microsoft.Extensions.Options.Options.Create(Options), Lease);
        Login = new(Store, Store, Lease, State, Time, NullLogger<SetupAccountLogin>.Instance);
    }

    public Task InitializeAsync(string password = ORIGINAL_PASSWORD)
    {
        SetupInitialization initialization = new(Store, Store, Microsoft.Extensions.Options.Options.Create(new BootstrapAdminOptions
        {
            Login = "local-admin", Password = password
        }), Lease, Preflight, State, Secrets, Time, NullLogger<SetupInitialization>.Instance);
        return initialization.InitializeAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        Lease.Dispose();
        if (!Path.GetFullPath(Root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("InvalidTestDirectory");
        }

        Directory.Delete(Root, true);
    }

    public class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }

    private class NoNetworkClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            throw new InvalidOperationException("NetworkClientNotExpected");
        }
    }

    public class Inventory : IInstallationInventory
    {
        public bool Current { get; set; } = true;
        public Dictionary<string, bool> Tables { get; } = new();
        public Task<InstallationInventory> ReadAsync(CancellationToken token)
        {
            return Task.FromResult(new InstallationInventory(Current, Tables));
        }
    }

    public class MemoryStore : ILocalSetupUnitOfWork, ILocalInstallationRepository, ILocalSetupAccountStore
    {
        public LocalInstallationProfile? Profile { get; set; }
        public LocalSetupAccount? Account { get; set; }
        public Dictionary<Guid, LocalAttemptStep> Steps { get; } = new();
        public bool Progress { get; set; }
        public int Commits { get; private set; }
        public Action? BeforeCommit { get; set; }
        public ILocalInstallationRepository Profiles => this;

        public Task<LocalInstallationProfile?> ReadAsync(CancellationToken token)
        {
            return Task.FromResult(Profile);
        }

        Task<LocalSetupAccount?> ILocalSetupAccountStore.ReadAsync(CancellationToken token)
        {
            return Task.FromResult(Account);
        }

        public void Create(LocalInstallationProfile profile)
        {
            Profile = profile;
        }

        public void Create(LocalSetupAccount account)
        {
            Account = account;
        }

        public Task<bool> HasProgressAsync(CancellationToken token)
        {
            return Task.FromResult(Progress);
        }

        public Task ValidateProgressAsync(LocalInstallationProfile profile, CancellationToken token)
        {
            return Task.CompletedTask;
        }

        public Task ReplaceAsync(LocalInstallationProfile profile, long expectedRevision, CancellationToken token)
        {
            if (Profile?.ProfileRevision != expectedRevision)
            {
                throw new InvalidOperationException("RevisionConflict");
            }

            Profile = profile;
            return Task.CompletedTask;
        }

        public Task<LocalAttemptStep?> FindStepAsync(Guid operationId, CancellationToken token)
        {
            return Task.FromResult(Steps.GetValueOrDefault(operationId));
        }

        public void AddStep(LocalAttemptStep step)
        {
            Steps.Add(step.OperationId, step);
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            BeforeCommit?.Invoke();
            Commits++;
            return Task.FromResult(1);
        }

        public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
        {
            LocalInstallationProfile? previous = Profile;
            try
            {
                await action(cancellationToken);
                await SaveChangesAsync(cancellationToken);
            }
            catch
            {
                Profile = previous;
                throw;
            }
        }
    }
}
