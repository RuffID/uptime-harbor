using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Issuers;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Web.Cluster;
using UptimeHarbor.Web.Controllers;

namespace UptimeHarbor.Tests.Web.Cluster.Issuers;

/// <summary>Работающий manual образ; настоящие crypto и pipeline, SQL/native/HTTP заменены.</summary>
public class IssuerRegistrationFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "manual-issuer-" + Guid.NewGuid().ToString("N"));
    public Guid Actor { get; } = Guid.NewGuid();
    public Guid Remote { get; } = Guid.NewGuid();
    public Guid RemoteCredential { get; } = Guid.NewGuid();
    public ClusterOptions Options { get; }
    public InstallationOptions Local { get; }
    public RevisionStateStore State { get; } = new();
    public InvitationHttpFixture.Clock Time { get; } = new();
    public InvitationHttpFixture.Login Passwords { get; } = new();
    public InvitationHttpFixture.MemoryPending Work { get; } = new();
    public InvitationHttpFixture.Projection Projection { get; } = new();
    public SigningWork Signing { get; } = new();
    public User CurrentUser { get; }
    public View Runtime { get; }
    public NativeProbe Native { get; }
    public NodeReadiness Readiness { get; }
    public ClusterRaftHandle Handle { get; } = new();
    public PeerHandler Peer { get; }
    public EnrollmentIssuerStorage Storage { get; }
    public EnrollmentIssuerProvisioning Provisioning { get; }
    public EnrollmentIssuerRegistrationBarrier Barrier { get; }
    public OnboardingAcceptance Acceptance { get; }
    public RevisionDispatcher Dispatcher { get; }
    public PendingCommandService Pending { get; }
    public Transport Delivery { get; }
    public ClusterEnrollmentIssuersController Controller { get; }
    public CapturedLog<OnboardingAudit> Audit { get; } = new();
    public Protection Payloads { get; } = new();
    public List<PeerCredential> VoterCredentials { get; } = [];
    private readonly HttpClient client;
    private readonly ConsensusOnlyState nativeState = new();

    public IssuerRegistrationFixture()
    {
        Options = new()
        {
            Mode = "Restart", ClusterId = Guid.NewGuid(), Generation = 1, NodeId = Guid.NewGuid(), StorageGeneration = Guid.NewGuid(),
            CredentialId = Guid.NewGuid(), StoragePath = Path.Combine(Root, "storage"),
            ProtocolMajor = 3, CommandSchema = 3, SnapshotFormat = 3, SqlSchema = 3,
            PublicUri = "https://local.invalid/internal/raft", ClientCaPath = Path.Combine(Root, "operator-ca.crt"),
            ClientKeyPath = Path.Combine(Root, "legacy.key"), ClientCaFingerprint = new string('C', 64)
        };
        Local = new()
        {
            StateDirectory = Path.Combine(Root, "state"), SecretDirectory = Path.Combine(Root, "secret"),
            KeyDirectory = Path.Combine(Root, "keys")
        };
        CurrentUser = new(Actor);
        AcceptedValue actor = new(Actor, DeletedEntityKind.Account, 1, new ConfigurationValue
        {
            Name = "admin", RoleId = Guid.NewGuid(), Enabled = true, PasswordHash = "test-hash",
            SecurityStamp = "test-stamp", ConcurrencyStamp = "test-stamp"
        });
        OperationalNode local = new(Options.NodeId, Options.StorageGeneration, Options.PublicUri, "local", true, true, false, 1);
        OperationalNode remote = new(Remote, Guid.NewGuid(), "https://remote.invalid/internal/raft", "remote", true, true, false, 1);
        OperationalCredential credential = new(Options.CredentialId, local.NodeId, new string('A', 64),
            Convert.ToBase64String([1, 2, 3]), Time.Now.AddYears(1), null, null, null);
        State.Publish(new RevisionImage(Options.ClusterId, 1)
        {
            Index = 2, Term = 1, Digest = new string('B', 64), Values = ImmutableDictionary<Guid, AcceptedValue>.Empty.Add(Actor, actor),
            Versions = ImmutableDictionary<string, AcceptedValue>.Empty.Add(RevisionReducer.VersionKey(Actor, 1), actor),
            Operations = new ClusterOperationsImage
            {
                Nodes = ImmutableDictionary<Guid, OperationalNode>.Empty.Add(local.NodeId, local).Add(Remote, remote),
                Credentials = ImmutableDictionary<Guid, OperationalCredential>.Empty.Add(credential.Id, credential),
                MembershipVersion = 1
            }
        });
        Runtime = new(State, new(Options.ClusterId, 1, Options.NodeId, 1, 1, true, 2,
            new HashSet<Guid> { Options.NodeId, Remote }, CancellationToken.None, CancellationToken.None));
        Readiness = new(Time);
        foreach (string component in new[] { "Database", "Projection", "Storage", "Clock" })
        {
            Readiness.Report(component, true, "Ready", TimeSpan.FromDays(1));
        }

        IRaftCluster raft = DispatchProxy.Create<IRaftCluster, NativeProbe>();
        Native = (NativeProbe)(object)raft;
        Native.State = State;
        ((IPersistentState)nativeState).UpdateTermAsync(1, true, CancellationToken.None).GetAwaiter().GetResult();
        Native.Store = nativeState;
        typeof(ClusterRaftHandle).GetProperty(nameof(ClusterRaftHandle.Current))!.SetValue(Handle, raft);
        Peer = new(this);
        VoterCredentials.Add(new(RemoteCredential, Remote, remote.StorageGeneration, new(remote.Address), new string('D', 64), true, false, Time.Now.AddYears(1)));
        Trust trust = new(VoterCredentials);
        PeerClientHandler authenticated = new(Microsoft.Extensions.Options.Options.Create(Options), trust, Time, Runtime) { InnerHandler = Peer };
        client = new(authenticated);
        EnrollmentReadBarrier majority = new(Handle, Runtime, State, Readiness, Microsoft.Extensions.Options.Options.Create(Options));
        Barrier = new(majority, Runtime, State, trust, new Clients(client), Microsoft.Extensions.Options.Options.Create(Options), Time);
        Storage = new(Microsoft.Extensions.Options.Options.Create(Local), Microsoft.Extensions.Options.Options.Create(Options));
        Provisioning = new(Signing, new(Signing, new()), Storage, new(), Microsoft.Extensions.Options.Options.Create(Options),
            Microsoft.Extensions.Options.Options.Create(Local), Time);
        AcceptedAdministratorAccessPolicy policy = new(CurrentUser, State);
        Pending = new(Work, CurrentUser, policy, State, Payloads, new PendingAudit(), new());
        Acceptance = new(Handle, Runtime, Readiness, Microsoft.Extensions.Options.Options.Create(Options),
            new UnavailableEnrollmentAuthority(), Time, State, issuerRegistration: Barrier);
        RevisionApplyEngine apply = new(State, new(), Projection);
        Dispatcher = new(new Consensus(State, apply, Options.NodeId), State, Runtime, time: Time,
            onboarding: Acceptance, onboardingAudit: new OnboardingAudit(Audit));
        Delivery = new(Dispatcher, Options.NodeId);
        EnrollmentIssuerAdministration administration = new(new(policy, CurrentUser, State), State, Pending, Work,
            Payloads, Delivery, Passwords, CurrentUser, Provisioning, Microsoft.Extensions.Options.Options.Create(Options),
            Microsoft.Extensions.Options.Options.Create(Local), Time, new CapturedLog<EnrollmentIssuerAdministration>(), Barrier);
        Controller = new(administration);
    }

    public void ChangeActor()
    {
        RevisionImage image = State.Current!;
        AcceptedValue actor = image.Values[Actor];
        State.Publish(image with { Values = image.Values.SetItem(Actor, actor with { Revision = actor.Revision + 1 }) });
    }

    public void Dispose()
    {
        client.Dispose();
        nativeState.Dispose();
        Storage.Dispose();
        if (Path.GetDirectoryName(Root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())
            || !Path.GetFileName(Root).StartsWith("manual-issuer-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("UnsafeTestCleanup");
        }

        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, true);
        }
    }

    public class View(RevisionStateStore state, ConsensusStatus initial) : IClusterStateView
    {
        public ConsensusStatus? Consensus { get; set; } = initial;
        public AcceptedClusterState Accepted => new(true, true, true, true, true, state.Current!.Index, 1, Guid.Empty, false);
    }

    public class User(Guid id) : IUserAccessContext
    {
        public Guid UserId => id;
        public bool IsAuthenticated { get; set; } = true;
        public bool IsEnabled => false;
        public bool IsAdministrator => false;
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

    private class PendingAudit : IRevisionAudit
    {
        public void Record(Guid operationId, Guid actorUserId, Guid nodeId, string outcome)
        {
        }
    }

    private class Consensus(RevisionStateStore state, RevisionApplyEngine engine, Guid node) : IConsensusWriter
    {
        public async ValueTask<long> ReplicateAsync(ReadOnlyMemory<byte> payload, int commandId, CancellationToken token)
        {
            long index = state.Current!.Index + 1;
            await engine.ApplyAsync(RevisionCodec.Decode(payload.Span), index, state.Current.Term, node, token);
            return index;
        }

        public ValueTask<bool> ResignAsync(CancellationToken token)
        {
            throw new NotSupportedException();
        }
    }

    public class Transport(RevisionDispatcher dispatcher, Guid node) : IRevisionTransport
    {
        public bool Offline { get; set; }
        public bool LoseResponse { get; set; }
        public Action? BeforeSend { get; set; }
        public List<byte[]> Sent { get; } = [];

        public async Task<IReadOnlyList<CommandResult>> SendAsync(IReadOnlyList<RevisionCommand> commands, CancellationToken token)
        {
            BeforeSend?.Invoke();
            Sent.Add(RevisionCodec.Encode(commands.Single()));
            if (Offline)
            {
                throw new HttpRequestException("TestTransportOffline");
            }

            CommandResult result = await dispatcher.AcceptAsync(commands.Single(), node, token);
            if (LoseResponse)
            {
                throw new HttpRequestException("TestLostResponse");
            }

            return [result];
        }
    }

    public class PeerHandler(IssuerRegistrationFixture fixture) : HttpMessageHandler
    {
        public Func<PeerHealthSnapshot, PeerHealthSnapshot>? Edit { get; set; }
        public Func<PeerEnvelope, PeerEnvelope>? EditEnvelope { get; set; }
        public Action? BeforeResponse { get; set; }
        public int Calls { get; private set; }
        public bool Oversize { get; set; }
        public HashSet<Guid> Contacted { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            PeerEnvelope received = PeerHeaders.Decode(request.Headers.GetValues(PeerHeaders.ENVELOPE).Single())!;
            PeerCredential target = fixture.VoterCredentials.Single(item => item.NodeId == received.ToNodeId);
            Contacted.Add(target.NodeId);
            PeerEnvelope reply = received with { FromNodeId = target.NodeId, ToNodeId = fixture.Options.NodeId,
                CredentialId = target.CredentialId, StorageGeneration = target.StorageGeneration };
            PeerHealthSnapshot health = new(target.NodeId, Guid.NewGuid(), fixture.Runtime.Consensus!.Term,
                fixture.Options.NodeId, fixture.Runtime.Consensus.MembershipVersion, 2, 2, false, 0, new Dictionary<string, HealthResult>())
            {
                EnrollmentTrustVersion = 1
            };
            if (Edit is not null)
            {
                health = Edit(health);
            }

            HttpContent content = JsonContent.Create(health);
            if (Oversize)
            {
                content = new ByteArrayContent(new byte[65537]);
            }

            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = content };
            if (EditEnvelope is not null)
            {
                reply = EditEnvelope(reply);
            }

            response.Headers.Add(PeerHeaders.ENVELOPE, PeerHeaders.Encode(reply));
            BeforeResponse?.Invoke();
            return Task.FromResult(response);
        }
    }

    private class Clients(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            if (name != PeerClientHandler.CLIENT_NAME)
            {
                throw new InvalidOperationException("UnexpectedTestClient");
            }

            return client;
        }
    }

    private class Trust(List<PeerCredential> voters) : IPeerTrustSource
    {
        public ValueTask<PeerCredential?> FindAsync(Guid id, CancellationToken token)
        {
            return ValueTask.FromResult(voters.SingleOrDefault(item => item.CredentialId == id));
        }

        public ValueTask<IReadOnlyList<PeerCredential>> ListAsync(CancellationToken token)
        {
            return ValueTask.FromResult<IReadOnlyList<PeerCredential>>(voters.ToArray());
        }
    }

    public class NativeProbe : ManagedRaftProbe
    {
        public RevisionStateStore State { get; set; } = null!;
        public int? FailAt { get; set; }
        public int Calls { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "ApplyReadBarrierAsync")
            {
                Calls++;
                if (FailAt.HasValue && Calls >= FailAt)
                {
                    throw new InvalidOperationException("EnrollmentReadMajorityUnavailable");
                }
            }

            return base.Invoke(method, args);
        }
    }

    public class SigningWork : IEnrollmentSigningUnitOfWork, IEnrollmentSigningRepository
    {
        public IEnrollmentSigningRepository Signing => this;
        public Dictionary<Guid, EnrollmentIssuerReference> Issuers { get; } = [];
        public Dictionary<Guid, EnrollmentIssuedArtifact> Issues { get; } = [];
        private EnrollmentIssuerReference? prepared;
        private EnrollmentIssuedArtifact? preparedIssue;
        public bool FailSave { get; set; }
        public Action? AfterSave { get; set; }
        public int Commits { get; private set; }

        public Task<IReadOnlyList<EnrollmentIssuerReference>> ListIssuersAsync(CancellationToken token)
        {
            return Task.FromResult<IReadOnlyList<EnrollmentIssuerReference>>(Issuers.Values.ToArray());
        }

        public Task<EnrollmentIssuerReference?> FindIssuerAsync(Guid epoch, CancellationToken token)
        {
            return Task.FromResult(Issuers.GetValueOrDefault(epoch));
        }

        public Task<EnrollmentIssuedArtifact?> FindIssueAsync(Guid id, CancellationToken token)
        {
            return Task.FromResult(Issues.GetValueOrDefault(id));
        }

        public void AddIssuer(EnrollmentIssuerReference issuer)
        {
            prepared = issuer;
        }

        public void AddIssue(EnrollmentIssuedArtifact issue)
        {
            preparedIssue = issue;
        }

        public Task<int> SaveChangesAsync(CancellationToken token = default)
        {
            if (FailSave)
            {
                prepared = null;
                throw new IOException("TestLocalCommitFailed");
            }

            if (prepared is not null)
            {
                Issuers.Add(prepared.Epoch, prepared);
                prepared = null;
            }

            if (preparedIssue is not null)
            {
                Issues.Add(preparedIssue.IssueId, preparedIssue);
                preparedIssue = null;
            }

            Commits++;
            AfterSave?.Invoke();
            return Task.FromResult(1);
        }

        public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken token = default)
        {
            await action(token);
            await SaveChangesAsync(token);
        }
    }

    public class CapturedLog<T> : ILogger<T>
    {
        public List<KeyValuePair<string, object?>[]> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel level)
        {
            return true;
        }

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToArray());
        }
    }
}
