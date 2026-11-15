using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Application.Users;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Web.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;
using UptimeHarbor.Web.Cluster.EnrollmentRouting;
using UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

namespace UptimeHarbor.Tests.Web.Cluster;

/// <summary>Production genesis/permit/recovery/signers/validators/lifecycle, локальный WAL, fake SQL/native calls.</summary>
public class ManagedBootstrapFixture : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "managed-bootstrap-" + Guid.NewGuid().ToString("N"));
    public ManagedBootstrapStore Store { get; } = new();
    public InstallationState Installation { get; } = new();
    public RevisionStateStore Accepted { get; } = new();
    public Clock Time { get; } = new();
    public InstallationLease Lease { get; }
    public ClusterOptions Options { get; }
    public ManagedAdmissionState Admission { get; }
    public OnlineEnableState Enable { get; }
    public NodeReadiness Readiness { get; }
    public ClusterRuntimeState Runtime { get; }
    public ManagedBootstrapLifecycle Driver { get; }
    public ManagedRaftProbe Native { get; }
    public ClusterAccountBootstrapper AccountBootstrap { get; }
    public GenesisWriterPermit Permit { get; } = new();
    public RevisionCommand Account { get; }
    public SetupAuthority Authority { get; }
    public IConsensusWriter Writer { get; }
    public RevisionDispatcher Dispatcher { get; }
    public EnrollmentAuthority EnrollmentAuthority { get; } = new();
    public EnrollmentNonces Nonces { get; }
    public EnrollmentGateway Enrollment { get; }
    public EnrollmentDescriptors Descriptors { get; }
    public InstallationOptions Local { get; }
    public DynamicPeerTls Tls => tls;
    public LocalSecretStore Secrets => secrets;
    public ClusterRaftHandle Handle { get; } = new();
    public EnrollmentSigner Issuer { get; }
    public OnlineGrantSigner Grants { get; }
    public EnrollmentIssuanceRouter Issuance { get; }
    public EnrollmentReadBarrier ReadBarrier { get; }
    public OnlineGrantRequests GrantRequests { get; }
    public ManagedStorageSeals? Seals { get; }
    public ManagedCheckpointProjection Projection { get; } = new();
    public RevisionSnapshotStore Snapshots { get; }
    public RevisionClusterRecovery Recovery => recovery;
    public Action? BeforeReadyPublication { get; set; }
    private readonly LocalSecretStore secrets;
    private readonly GenesisPreparation genesis;
    private readonly GenesisRuntimePreparation preparation;
    private readonly RevisionClusterRecovery recovery;
    private readonly ClusterStateMachine machine;
    private readonly DynamicPeerTls tls;
    private readonly ServiceProvider services;
    private WriteAheadLog? wal;

    public ManagedBootstrapFixture(bool durableRestart = false)
    {
        InstallationOptions local = new()
        {
            InstallationIntent = "Setup", StateDirectory = Path.Combine(Root, "state"),
            KeyDirectory = Path.Combine(Root, "keys"), SecretDirectory = Path.Combine(Root, "secret"),
            BrowserOrigin = "https://browser.example.test", PeerEndpoint = "https://peer.example.test:8443/internal/raft"
        };
        Local = local;
        Options = new() { StoragePath = Path.Combine(Root, "storage"), Mode = "Bootstrap", PublicUri = local.PeerEndpoint,
            ClusterId = Guid.NewGuid(), NodeId = Guid.NewGuid(), StorageGeneration = Guid.NewGuid(), Generation = 1,
            ExpectedBotId = 123, ProtocolMajor = 3, CommandSchema = 3, SnapshotFormat = 3, SqlSchema = 3 };
        Lease = new(local, Options.StoragePath);
        secrets = new(Microsoft.Extensions.Options.Options.Create(local), Lease);
        if (durableRestart)
        {
            Seals = new(Microsoft.Extensions.Options.Options.Create(local), Microsoft.Extensions.Options.Options.Create(Options), Installation, secrets);
        }
        Guid attempt = Guid.NewGuid();
        Guid actor = Guid.NewGuid();
        Account = new()
        {
            ClusterId = Options.ClusterId, ClusterGeneration = 1, OperationId = Guid.NewGuid(), OriginNodeId = Options.NodeId,
            ActorUserId = actor, ActorRevision = 1, EntityId = actor, EntityKind = DeletedEntityKind.Account,
            Action = "BootstrapAccount", BaseRevision = 0, CreatedAtUtc = Time.Now,
            Value = new() { Name = "common-admin", RoleId = Guid.NewGuid(), PasswordHash = "test-hash",
                SecurityStamp = "test-stamp", ConcurrencyStamp = "test-concurrency" }
        };
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] peer = Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem());
        string peerReference;
        try
        {
            peerReference = secrets.Write(attempt, "peer", peer);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(peer);
        }

        Store.Profile = new(Lease.InstallIdentity, "Bootstrap", InstallationPhase.LocalPrepared, 1, 1, 1, Lease.BootId, Time.Now.AddMinutes(2))
        {
            ClusterId = Options.ClusterId, ClusterGeneration = 1, AttemptId = attempt, NodeId = Options.NodeId,
            StorageGeneration = Options.StorageGeneration, PeerAddress = Options.PublicUri, KeyReference = peerReference,
            BootstrapAccountReference = secrets.Write(attempt, "bootstrap", RevisionCodec.Encode(Account)),
            BootstrapAccountDigest = RevisionCodec.Digest(Account), BotId = 123,
            OperationIds = new Dictionary<string, Guid> { ["bootstrap"] = Account.OperationId, ["admit"] = Guid.NewGuid() }.ToImmutableDictionary()
        };
        Installation.Publish(Store.Profile);
        genesis = new(Store, Store, secrets, Lease, Microsoft.Extensions.Options.Options.Create(local), Time);
        Admission = new(Installation, Microsoft.Extensions.Options.Options.Create(local), Microsoft.Extensions.Options.Options.Create(new ClusterControlOptions()), Lease);
        Readiness = new(Time);
        Enable = new(Accepted, Installation, Lease, Readiness, Time);
        ClusterLocalControl control = new(Microsoft.Extensions.Options.Options.Create(Options), Microsoft.Extensions.Options.Options.Create(new ClusterControlOptions()), Time, Readiness, Admission, Enable);
        ManagedRecoveryAdmission source = new(Microsoft.Extensions.Options.Options.Create(local), Admission,
            new SignedRecoveryAdmission(control, Microsoft.Extensions.Options.Options.Create(Options), Time));
        services = new ServiceCollection().BuildServiceProvider();
        Snapshots = new(Microsoft.Extensions.Options.Options.Create(Options));
        recovery = new(source, Accepted, new(Accepted, new(), Projection), Projection,
            Snapshots, services.GetRequiredService<IServiceScopeFactory>(), Microsoft.Extensions.Options.Options.Create(Options), managedSeals: Seals, time: Time);
        Runtime = new(recovery, Accepted, control, Microsoft.Extensions.Options.Options.Create(Options), Time);
        machine = new(recovery, Readiness);
        tls = new(Installation, Accepted, Time);
        preparation = new(Store, Store, secrets, Installation, Lease, tls, Admission, Permit, Accepted,
            Microsoft.Extensions.Options.Options.Create(Options), Time);
        IRaftCluster raft = DispatchProxy.Create<IRaftCluster, ManagedRaftProbe>();
        Native = (ManagedRaftProbe)raft;
        ClusterRaftHandle handle = Handle;
        typeof(ClusterRaftHandle).GetProperty(nameof(ClusterRaftHandle.Current))!.SetValue(handle, raft);
        ClusterConsensusWriter writer = new(handle, Runtime, Readiness, Microsoft.Extensions.Options.Options.Create(Options), Accepted, Permit);
        Writer = writer;
        OnboardingAcceptance acceptance = new(handle, Runtime, Readiness, Microsoft.Extensions.Options.Options.Create(Options),
            EnrollmentAuthority, Time, Accepted, Permit);
        RevisionDispatcher dispatcher = new(writer, Accepted, Runtime, time: Time, onboarding: acceptance);
        Dispatcher = dispatcher;
        Nonces = new(Time);
        EnrollmentSigner issuer = new(Store, new(Store, new()), new(),
            new(Microsoft.Extensions.Options.Options.Create(local), Microsoft.Extensions.Options.Options.Create(Options), Lease, secrets),
            Time, NullLogger<EnrollmentSigner>.Instance,
            new EnrollmentSigningAuthorization(handle, Runtime, Accepted, Readiness, Microsoft.Extensions.Options.Options.Create(Options), Time));
        Issuer = issuer;
        ReadBarrier = new(handle, Runtime, Accepted, Readiness, Microsoft.Extensions.Options.Options.Create(Options));
        OnlineGrantSigningAuthorization signing = new(handle, Runtime, Accepted, Readiness,
            Microsoft.Extensions.Options.Options.Create(Options), Time, Admission, Lease);
        Grants = new(Store, signing, new(),
            new(Microsoft.Extensions.Options.Options.Create(local), Microsoft.Extensions.Options.Options.Create(Options), Lease, secrets), Time);
        EnrollmentCommandReceiver receiver = new(EnrollmentAuthority, dispatcher, Accepted, Runtime, ReadBarrier,
            Microsoft.Extensions.Options.Options.Create(Options), Microsoft.Extensions.Options.Options.Create(local), Time);
        EnrollmentPeerClient peers = new(handle, Runtime, Accepted, ReadBarrier, receiver, new AcceptedPeerTrustSource(Accepted, Time),
            new NoNetworkPeerClients(), Microsoft.Extensions.Options.Options.Create(Options), Time);
        GrantRequests = new(Store, Installation, Lease, secrets, Admission, Readiness, Accepted,
            Microsoft.Extensions.Options.Options.Create(Options), Time);
        Issuance = new(issuer, Grants, Accepted, peers, Microsoft.Extensions.Options.Options.Create(Options), Time, GrantRequests);
        Descriptors = new(Accepted, Microsoft.Extensions.Options.Options.Create(local), Microsoft.Extensions.Options.Options.Create(Options), Time);
        Enrollment = new(Descriptors,
            Nonces, new(), new(Store, Installation, Time), peers, Issuance,
            Microsoft.Extensions.Options.Options.Create(Options), Microsoft.Extensions.Options.Options.Create(local), Time,
            ReadBarrier);
        AccountBootstrap = new(Microsoft.Extensions.Options.Options.Create(Options), new UnusedCredentials(), source, Accepted, writer, new Audit(), dispatcher);
        Authority = new(genesis, Enable);
        InstallationLifecycle lifecycle = new(Store, Store, Installation, new ReadyRuntime(Installation, Enable,
            () => BeforeReadyPublication?.Invoke()), Authority,
            Lease, Time, NullLogger<InstallationLifecycle>.Instance);
        Driver = new(Installation, Lease, Admission, Store, Issuance,
            new(Store, Installation, Lease, Accepted, secrets, Time), Enable, Accepted, Runtime, handle, Readiness,
            dispatcher, lifecycle, Microsoft.Extensions.Options.Options.Create(Options), Time, recovery, Seals);
    }

    public async Task StartQuarantineAsync(CancellationToken token)
    {
        LocalInstallationProfile profile = Store.Profile!;
        ServiceResult<SetupAuthorityEvidence> result = await Authority.PrepareBootstrapAsync(new(profile.InstallIdentity,
            profile.AttemptId!.Value, profile.ClusterId!.Value, profile.NodeId!.Value, profile.StorageGeneration!.Value,
            profile.PeerAddress!, profile.BootstrapAccountDigest!, profile.BootstrapAccountReference!, profile.BotId,
            profile.ProfileRevision, profile.RuntimeGeneration), token);
        SetupAuthorityEvidence evidence = result.Data!;
        Options.CredentialId = evidence.Genesis!.Template.IssueId;
        Options.ClientCaFingerprint = evidence.Genesis.Authorization.IssuerCertificateDigest;
        Store.Profile = profile with { Evidence = JsonSerializer.Serialize(evidence), ProfileRevision = 2,
            CredentialId = Options.CredentialId, TrustAnchorFingerprint = Options.ClientCaFingerprint, Phase = InstallationPhase.ClusterQuarantine };
        Installation.Publish(Store.Profile);
        if (!await preparation.PrepareAsync(Store.Profile, token) || !await machine.RestoreAsync(Options, token))
        {
            throw new InvalidOperationException("TestGenesisPreparationFailed");
        }

        wal = new(new WriteAheadLog.Options { Location = Path.Combine(Options.StoragePath, "wal"),
            FlushInterval = TimeSpan.Zero, FlushOnCommit = true, HashAlgorithm = WriteAheadLog.IntegrityHashAlgorithm.Crc64 }, machine);
        await wal.InitializeAsync(token);
        if (Seals is not null)
        {
            ProtectedInstallationFiles.WriteNew(Path.Combine(Options.StoragePath, "started"), [1]);
            ProtectedInstallationFiles.WriteNew(Path.Combine(Options.StoragePath, "membership"), BitConverter.GetBytes(17L));
        }
        await ((IPersistentState)wal).UpdateTermAsync(1, true, token);
        Native.Store = wal;
        Native.Replicate = async (entry, cancellation) =>
        {
            long index = await wal.AppendAsync(entry, cancellation);
            await wal.CommitAsync(index, cancellation);
            await wal.WaitForApplyAsync(index, cancellation);
            await RefreshAsync(cancellation);
        };
        await RefreshAsync(token);
    }

    public async Task RefreshAsync(CancellationToken token)
    {
        recovery.VerifyNativeMembership(17, new HashSet<Guid> { Options.NodeId }, false);
        bool replay = await recovery.ConfirmLocalReplayAsync(wal!.LastCommittedEntryIndex, wal.LastAppliedIndex, 17, token);
        machine.ConfirmReplay(replay);
        foreach (string component in new[] { "Database", "Projection", "Storage", "Clock" })
        {
            Readiness.Report(component, true, "Ready", TimeSpan.FromMinutes(5));
        }

        Runtime.Publish(new(Options.ClusterId, 1, Options.NodeId, ((IPersistentState)wal).Term, 17, true, wal.LastCommittedEntryIndex,
            new HashSet<Guid> { Options.NodeId }, CancellationToken.None, CancellationToken.None));
        Readiness.ConfirmRecovery(Readiness.RecoveryVersion, Runtime.Accepted, Runtime.Consensus!);
    }

    public async ValueTask DisposeAsync()
    {
        if (wal is not null)
        {
            await wal.DisposeAsync();
        }

        tls.Revoke();
        await services.DisposeAsync();
        Lease.Dispose();
        if (Path.GetDirectoryName(Root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())
            || !Path.GetFileName(Root).StartsWith("managed-bootstrap-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("UnsafeTestCleanup");
        }

        Directory.Delete(Root, true);
    }

    public async Task StopStorageAsync()
    {
        await wal!.DisposeAsync();
        wal = null;
        Lease.Dispose();
        tls.Revoke();
    }

    public class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }

    private class ReadyRuntime(InstallationState state, OnlineEnableState enable, Action beforePublish) : IInstallationRuntime
    {
        public Task<bool> DrainAsync(long expectedGeneration, TimeSpan budget, CancellationToken token)
        {
            throw new NotSupportedException();
        }

        public Task StartQuarantinedAsync(LocalInstallationProfile profile, CancellationToken token)
        {
            throw new NotSupportedException();
        }

        public Task PublishReadyAsync(LocalInstallationProfile profile, CancellationToken token)
        {
            beforePublish();
            if (state.Current != profile || !enable.Allows())
            {
                throw new InvalidOperationException("TestReadyAuthorityMissing");
            }

            return Task.CompletedTask;
        }
    }

    private class Audit : IRevisionAudit
    {
        public void Record(Guid operationId, Guid actorUserId, Guid nodeId, string outcome)
        {
        }
    }

    private class UnusedCredentials : IAccountCredentialService
    {
        public Task<ServiceResult<ProtectedCredential>> PrepareAsync(Guid userId, string login, string password, CancellationToken token)
        {
            throw new NotSupportedException();
        }

        public bool Verify(Guid userId, string login, ProtectedCredential credential, string password)
        {
            throw new NotSupportedException();
        }
    }

}
