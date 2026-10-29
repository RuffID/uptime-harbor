using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Application.Users;
using UptimeHarbor.Application.Users.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Tests.Application.Cluster;

namespace UptimeHarbor.Tests.Application.Users;

public class SharedAccountFixture
{
    public RevisionScenario Scenario { get; } = new();
    public RevisionStateStore State { get; } = new();
    public MemoryPending Work { get; } = new();
    public Protection Payloads { get; } = new();
    public Credentials Passwords { get; } = new();
    public Audit Events { get; } = new();
    private readonly PendingAdmissionGate pendingGate = new();
    private readonly AccountCommandGate accountGate = new();
    public SharedUserManagementService Service { get; }

    public SharedAccountFixture()
    {
        State.Publish(Scenario.State);
        Service = Create(Scenario.Actor);
    }

    public SharedUserManagementService Create(Guid actor)
    {
        User user = new(actor);
        AcceptedAdministratorAccessPolicy policy = new(user, State);
        PendingCommandService pending = new(Work, user, policy, State, Payloads, Events, pendingGate);
        return new(user, policy, State, pending, Work, Payloads, Passwords, TimeProvider.System, accountGate);
    }

    public RevisionCommand Command(Guid operationId)
    {
        return RevisionCodec.Decode(Payloads.Unprotect(Work.Items[operationId].ProtectedPayload));
    }

    public CommandResult Commit(Guid operationId)
    {
        RevisionCommand command = Command(operationId);
        Scenario.State = State.Current!;
        CommandResult result = Scenario.Apply(command);
        State.Publish(Scenario.State);
        return result;
    }

    public class User(Guid id) : IUserAccessContext
    {
        public bool IsAuthenticated => true;
        public bool IsAdministrator => false;
        public bool IsEnabled => false;
        public Guid UserId => id;
    }

    public class Credentials : IAccountCredentialService
    {
        private readonly Dictionary<string, string> passwords = [];
        public int Preparations { get; private set; }

        public Task<ServiceResult<ProtectedCredential>> PrepareAsync(Guid userId, string login, string password, CancellationToken token)
        {
            Preparations++;
            string hash = "prepared-" + Guid.NewGuid().ToString("N");
            passwords.Add(hash, password);
            return Task.FromResult(ServiceResult<ProtectedCredential>.Ok(new(hash, 1,
                Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"))));
        }

        public bool Verify(Guid userId, string login, ProtectedCredential credential, string password)
        {
            return passwords.GetValueOrDefault(credential.PasswordHash) == password;
        }
    }

    public class Protection : IRevisionPayloadProtection
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

    public class Audit : IRevisionAudit
    {
        public List<string> Outcomes { get; } = [];
        public void Record(Guid operationId, Guid actorUserId, Guid nodeId, string outcome)
        {
            Outcomes.Add(outcome);
        }
    }

    public class MemoryPending : IPendingControlUnitOfWork, IPendingOperationRepository
    {
        public Dictionary<Guid, PendingOperation> Items { get; } = [];
        public IPendingOperationRepository PendingOperations => this;

        public Task<PendingOperation?> GetAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Items.GetValueOrDefault(operationId));
        }

        public void Create(PendingOperation operation)
        {
            Items.Add(operation.Id, operation);
        }

        public Task<IReadOnlyList<PendingOperation>> GetPageAsync(int offset, int limit, bool readyOnly,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<PendingOperation>>(Items.Values.Skip(offset).Take(limit).ToArray());
        }

        public Task<IReadOnlyList<PendingOperation>> GetDueAsync(bool priority, DateTimeOffset nowUtc, int limit,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<PendingOperation>>([]);
        }

        public Task<PendingBacklog> GetBacklogAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PendingBacklog(Items.Count, 0, null));
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(1);
        }

        public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
        {
            await action(cancellationToken);
        }
    }
}
