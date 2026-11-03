using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Web.Controllers;

namespace UptimeHarbor.Tests.Web.Cluster;

/// <summary>Настоящие Pending/dispatcher/reducer/apply с изолированными временем, SQL и transport.</summary>
public class InvitationHttpFixture
{
    public Guid Actor { get; } = Guid.NewGuid();
    public Guid Node { get; } = Guid.NewGuid();
    public Guid Storage { get; } = Guid.NewGuid();
    public RevisionStateStore State { get; } = new();
    public MemoryPending Work { get; } = new();
    public Clock Time { get; } = new();
    public Login Passwords { get; } = new();
    public Projection Store { get; } = new();
    public Log AuditLog { get; } = new();
    public Transport Delivery { get; }
    public ClusterInvitationsController Controller { get; }
    public ClusterOptions Options { get; }

    public InvitationHttpFixture(bool authenticated = true, bool enabled = true)
    {
        AcceptedValue actor = new(Actor, DeletedEntityKind.Account, 1, new ConfigurationValue
        {
            Name = "admin", RoleId = Guid.NewGuid(), Enabled = enabled, PasswordHash = "test-hash",
            SecurityStamp = "test-stamp", ConcurrencyStamp = "test-stamp"
        });
        OperationalNode local = new(Node, Storage, "https://first.invalid/internal/raft", "first", true, true, false, 1);
        State.Publish(new RevisionImage(Guid.NewGuid(), 1)
        {
            Index = 2, Term = 1, Digest = new string('A', 64), Values = ImmutableDictionary<Guid, AcceptedValue>.Empty.Add(Actor, actor),
            Versions = ImmutableDictionary<string, AcceptedValue>.Empty.Add(RevisionReducer.VersionKey(Actor, 1), actor),
            Operations = new ClusterOperationsImage { Nodes = new ClusterOperationsImage().Nodes.Add(Node, local), MembershipVersion = 1 },
            Monitoring = new MonitoringImage { Nodes = new MonitoringImage().Nodes.Add(Node, Storage) }
        });
        Options = new ClusterOptions
        {
            Mode = "Restart", NodeId = Node, StorageGeneration = Storage, ProtocolMajor = 2, CommandSchema = 2, SnapshotFormat = 2, SqlSchema = 2
        };
        User user = new(Actor, authenticated);
        AcceptedAdministratorAccessPolicy policy = new(user, State);
        ClusterAdministrationAccess access = new(policy, user, State);
        Protection protection = new();
        PendingCommandService pending = new(Work, user, policy, State, protection, new PendingAudit(), new PendingAdmissionGate());
        RevisionApplyEngine engine = new(State, new RevisionReducer(), Store);
        RevisionDispatcher dispatcher = new(new Consensus(State, engine, Node), State, time: Time,
            onboarding: new Acceptance(Time), onboardingAudit: new OnboardingAudit(AuditLog));
        Delivery = new(dispatcher, Node);
        InvitationAdministration administration = new(access, State, pending, Work, protection, Delivery, Passwords, user,
            Microsoft.Extensions.Options.Options.Create(Options), Time, new OnboardingAudit(AuditLog));
        Controller = new(administration);
    }

    public void ChangeActor()
    {
        RevisionImage image = State.Current!;
        AcceptedValue actor = image.Values[Actor];
        State.Publish(image with { Values = image.Values.SetItem(Actor, actor with { Revision = actor.Revision + 1 }) });
    }

    public class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }

    private class User(Guid id, bool authenticated) : IUserAccessContext
    {
        public Guid UserId => id;
        public bool IsAuthenticated => authenticated;
        public bool IsEnabled => false;
        public bool IsAdministrator => false;
    }

    public class Login : ILocalAccountLogin
    {
        public Action? BeforeReturn { get; set; }
        public int Checks { get; private set; }

        public Task<bool> CheckAsync(SharedAccount account, string password, CancellationToken token)
        {
            Checks++;
            BeforeReturn?.Invoke();
            return Task.FromResult(password == "test-password");
        }
    }

    private class Protection : IRevisionPayloadProtection
    {
        public string Protect(ReadOnlySpan<byte> payload)
        {
            return Convert.ToBase64String(payload);
        }

        public byte[] Unprotect(string payload)
        {
            return Convert.FromBase64String(payload);
        }
    }

    private class PendingAudit : IRevisionAudit
    {
        public void Record(Guid operationId, Guid actorUserId, Guid nodeId, string outcome)
        {
        }
    }

    private class Acceptance(Clock time) : IOnboardingAcceptance
    {
        public ValueTask<RevisionCommand> PrepareAsync(RevisionCommand command, CancellationToken token)
        {
            if (OnboardingCommands.NeedsEnrollmentAuthority(command))
            {
                throw new InvalidOperationException("NoTestAdmission");
            }

            return ValueTask.FromResult(command with { Onboarding = command.Onboarding! with { CoordinatorAtUtc = time.Now, CoordinatorTerm = 1 } });
        }
    }

    private class Consensus(RevisionStateStore state, RevisionApplyEngine engine, Guid node) : IConsensusWriter
    {
        public async ValueTask<long> ReplicateAsync(ReadOnlyMemory<byte> payload, int commandId, CancellationToken token)
        {
            long index = state.Current!.Index + 1;
            await engine.ApplyAsync(RevisionCodec.Decode(payload.Span), index, 1, node, token);
            return index;
        }

        public ValueTask<bool> ResignAsync(CancellationToken token)
        {
            throw new NotSupportedException();
        }
    }

    public class Transport(RevisionDispatcher dispatcher, Guid node) : IRevisionTransport
    {
        public bool DropResponse { get; set; }
        public bool NoLeader { get; set; }
        public int Sends { get; private set; }

        public async Task<IReadOnlyList<CommandResult>> SendAsync(IReadOnlyList<RevisionCommand> commands, CancellationToken token)
        {
            Sends++;
            if (NoLeader)
            {
                throw new InvalidOperationException("LeaderUnavailable");
            }

            List<CommandResult> results = [];
            foreach (RevisionCommand command in commands)
            {
                results.Add(await dispatcher.AcceptAsync(command, node, token));
            }

            if (DropResponse)
            {
                throw new HttpRequestException("LostTestResponse");
            }

            return results;
        }
    }

    public class Projection : IRevisionProjectionStore
    {
        public List<ProjectionWriteSet> Writes { get; } = [];
        public bool Fail { get; set; }

        public Task ApplyAsync(ProjectionWriteSet changes, CancellationToken token)
        {
            if (Fail)
            {
                throw new InvalidOperationException("TestProjectionCommitFailed");
            }

            Writes.Add(changes);
            return Task.CompletedTask;
        }

        public Task<ProjectionHead?> ReadHeadAsync(CancellationToken token)
        {
            return Task.FromResult<ProjectionHead?>(null);
        }

        public Task InstallAsync(RevisionImage image, Guid snapshotId, string snapshotDigest, CancellationToken token)
        {
            throw new NotSupportedException();
        }

        public Task VerifyAsync(RevisionImage image, CancellationToken token)
        {
            return Task.CompletedTask;
        }

        public Task<RevisionImage> ExportAsync(RevisionImage image, CancellationToken token)
        {
            return Task.FromResult(image);
        }

        public Task CheckpointAsync(Guid snapshotId, string digest, long index, CancellationToken token)
        {
            throw new NotSupportedException();
        }
    }

    public class Log : ILogger<OnboardingAudit>
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

        void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    public class MemoryPending : IPendingControlUnitOfWork, IPendingOperationRepository
    {
        public Dictionary<Guid, PendingOperation> Items { get; } = [];
        public Action? BeforeRead { get; set; }
        public IPendingOperationRepository PendingOperations => this;

        public Task<PendingOperation?> GetAsync(Guid id, CancellationToken token = default)
        {
            BeforeRead?.Invoke();
            return Task.FromResult(Items.GetValueOrDefault(id));
        }

        public void Create(PendingOperation operation)
        {
            Items.Add(operation.Id, operation);
        }

        public Task<IReadOnlyList<PendingOperation>> GetPageAsync(int offset, int limit, bool readyOnly, CancellationToken token = default)
        {
            return Task.FromResult<IReadOnlyList<PendingOperation>>(Items.Values.Skip(offset).Take(limit).ToArray());
        }

        public Task<IReadOnlyList<PendingOperation>> GetDueAsync(bool priority, DateTimeOffset now, int limit, CancellationToken token = default)
        {
            return Task.FromResult<IReadOnlyList<PendingOperation>>([]);
        }

        public Task<PendingBacklog> GetBacklogAsync(CancellationToken token = default)
        {
            return Task.FromResult(new PendingBacklog(Items.Count, 0, null));
        }

        public Task<int> SaveChangesAsync(CancellationToken token = default)
        {
            return Task.FromResult(1);
        }

        public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken token = default)
        {
            return action(token);
        }
    }
}
