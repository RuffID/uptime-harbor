using System.Reflection;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Web.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Issuers;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation.Renewal;
using UptimeHarbor.Web.Cluster.EnrollmentRouting;
using UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

namespace UptimeHarbor.Tests.Web.Cluster;

/// <summary>Новый process source и повторное открытие настоящего WAL; SQL/majority остаются doubles, native Start не вызывается.</summary>
public class ManagedRestartFixture : IAsyncDisposable
{
    public InstallationLease Lease { get; }
    public InstallationState Installation { get; } = new();
    public RevisionStateStore Accepted { get; } = new();
    public ManagedAdmissionState Admission { get; }
    public DynamicPeerTls Tls { get; }
    public ManagedStorageSeals Seals { get; }
    public ManagedRestartPreparation Preparation { get; }
    public NodeReadiness Readiness { get; }
    public ClusterRuntimeState Runtime { get; }
    public ManagedRaftProbe Native { get; }
    public OnlineEnableState Enable { get; }
    public ManagedBootstrapLifecycle Driver { get; }
    public RevisionClusterRecovery Recovery { get; }
    public IConsensusWriter Writer { get; }
    public RevisionDispatcher Dispatcher { get; }
    public LocalVoterCredentialPublication Publication { get; }
    public OnlineGrantRequests GrantRequests { get; }
    private readonly Func<LocalVoterRenewal> composeRenewal;
    private readonly ManagedRestartRotation.WalTransport renewalTransport;
    internal int RenewalSends => renewalTransport.Sends;
    private readonly ClusterStateMachine machine;
    private readonly ServiceProvider services = new ServiceCollection().BuildServiceProvider();
    private readonly ManagedBootstrapFixture previous;
    private WriteAheadLog? wal;

    public ManagedRestartFixture(ManagedBootstrapFixture previous)
    {
        this.previous = previous;
        Lease = new(previous.Local, previous.Options.StoragePath);
        LocalInstallationProfile old = previous.Store.Profile!;
        previous.Store.Profile = old with { BootId = Lease.BootId, ManagedRestart = true,
            Phase = InstallationPhase.ClusterQuarantine, RuntimeGeneration = old.RuntimeGeneration + 1,
            ProfileRevision = old.ProfileRevision + 1, LeaseUntilUtc = previous.Time.Now.AddMinutes(2) };
        Installation.Publish(previous.Store.Profile);
        previous.Options.Mode = "Restart";
        var configured = Options.Create(previous.Options);
        var local = Options.Create(previous.Local);
        LocalSecretStore secrets = new(local, Lease);
        Seals = new(local, configured, Installation, secrets);
        Admission = new(Installation, local, Options.Create(new ClusterControlOptions()), Lease);
        Readiness = new(previous.Time);
        Tls = new(Installation, Accepted, previous.Time);
        Enable = new(Accepted, Installation, Lease, Readiness, previous.Time, Tls);
        ClusterLocalControl control = new(configured, Options.Create(new ClusterControlOptions()), previous.Time, Readiness, Admission, Enable);
        ManagedRecoveryAdmission source = new(local, Admission, new(control, configured, previous.Time));
        Projection projection = new(previous.Accepted.Current!);
        RevisionSnapshotStore snapshots = new(configured);
        Recovery = new(source, Accepted, new(Accepted, new(), projection), projection, snapshots,
            services.GetRequiredService<IServiceScopeFactory>(), configured, managedSeals: Seals, time: previous.Time);
        machine = new(Recovery, Readiness);
        Runtime = new(Recovery, Accepted, control, configured, previous.Time, Installation, Tls);
        IRaftCluster raft = DispatchProxy.Create<IRaftCluster, ManagedRaftProbe>();
        Native = (ManagedRaftProbe)raft;
        ClusterRaftHandle handle = new();
        typeof(ClusterRaftHandle).GetProperty(nameof(ClusterRaftHandle.Current))!.SetValue(handle, raft);
        GenesisWriterPermit permit = new();
        ClusterConsensusWriter writer = new(handle, Runtime, Readiness, configured, Accepted, permit);
        Writer = writer;
        EnrollmentAuthority enrollment = new();
        OnboardingAcceptance acceptance = new(handle, Runtime, Readiness, configured, enrollment, previous.Time, Accepted, permit);
        RevisionDispatcher dispatcher = new(writer, Accepted, Runtime, time: previous.Time, onboarding: acceptance);
        Dispatcher = dispatcher;
        OnlineGrantSigningAuthorization authority = new(handle, Runtime, Accepted, Readiness, configured, previous.Time, Admission, Lease);
        EnrollmentReadBarrier barrier = new(handle, Runtime, Accepted, Readiness, configured);
        VoterCredentialRenewalAuthority renewalAuthority = new(Accepted, barrier, new(previous.Time), configured, previous.Time);
        LocalVoterCredentialFiles files = new(local, secrets);
        LocalVoterCredentialPreparation materials = new(files, secrets, Lease, Installation, renewalAuthority, previous.Time,
            NullLogger<LocalVoterCredentialPreparation>.Instance);
        LocalVoterCredentialBindingCoordinator binding = new(materials, files, Installation, renewalAuthority,
            new NoBind(), previous.Time, NullLogger<LocalVoterCredentialBindingCoordinator>.Instance);
        Publication = new(new(binding, files, Installation, NullLogger<LocalVoterCredentialInstallationCoordinator>.Instance),
            binding, files, Lease, Installation, Accepted, Tls);
        Preparation = new(Seals, snapshots, projection, Installation, Lease, secrets, Admission, Tls, configured,
            previous.Time, Accepted, Publication);
        EnrollmentCommandReceiver receiver = new(enrollment, dispatcher, Accepted, Runtime, barrier, configured, local, previous.Time);
        EnrollmentPeerClient peers = new(handle, Runtime, Accepted, barrier, receiver, new AcceptedPeerTrustSource(Accepted, previous.Time),
            new NoNetworkPeerClients(), configured, previous.Time);
        EnrollmentIssuerStorage storage = new(local, configured, Lease, secrets);
        EnrollmentSigner issuer = new(previous.Store, new(previous.Store, new()), new(), storage, previous.Time,
            NullLogger<EnrollmentSigner>.Instance, new EnrollmentSigningAuthorization(handle, Runtime, Accepted, Readiness, configured, previous.Time));
        OnlineGrantSigner grantSigner = new(previous.Store, authority, new(), storage, previous.Time);
        GrantRequests = new(previous.Store, Installation, Lease, secrets, Admission, Readiness, Accepted, configured, previous.Time, Tls);
        EnrollmentIssuanceRouter routing = new(issuer, grantSigner, Accepted, peers, configured, previous.Time, GrantRequests);
        SetupAuthority setup = new(new(previous.Store, previous.Store, secrets, Lease, local, previous.Time), Enable);
        InstallationLifecycle lifecycle = new(previous.Store, previous.Store, Installation, new ReadyRuntime(Enable), setup,
            Lease, previous.Time, NullLogger<InstallationLifecycle>.Instance);
        Driver = new(Installation, Lease, Admission, previous.Store, routing,
            new(previous.Store, Installation, Lease, Accepted, secrets, previous.Time, Tls), Enable, Accepted, Runtime, handle, Readiness,
            dispatcher, lifecycle, configured, previous.Time, Recovery, Seals, Publication, Tls);
        renewalTransport = new(Native, Accepted, previous.Time);
        composeRenewal = () =>
        {
            VoterCredentialSigner signer = new(previous.Store, new(previous.Store, new()), new(), storage, renewalAuthority,
                configured, previous.Time, NullLogger<VoterCredentialSigner>.Instance);
            VoterCredentialIssuanceRouter issuance = new(signer, peers, renewalAuthority, configured, previous.Time);
            LocalVoterCredentialBindingCoordinator liveBinding = new(materials, files, Installation, renewalAuthority,
                renewalTransport, previous.Time, NullLogger<LocalVoterCredentialBindingCoordinator>.Instance);
            LocalVoterCredentialInstallationCoordinator installed = new(liveBinding, files, Installation,
                NullLogger<LocalVoterCredentialInstallationCoordinator>.Instance);
            LocalVoterCredentialPublication livePublication = new(installed, liveBinding, files, Lease, Installation, Accepted, Tls);
            LocalVoterCredentialRetirement retirement = new(files, installed, Tls, renewalAuthority, renewalTransport, previous.Time);
            return new(Lease, Installation, Tls, materials, renewalAuthority, renewalTransport, issuance, liveBinding, installed,
                livePublication, configured, previous.Time, retirement);
        };
    }

    internal Task<DynamicPeerTls.Snapshot?> RunScheduledRenewalAsync(CancellationToken token)
    {
        return composeRenewal().RunScheduledAsync(token);
    }

    public async Task RestoreAsync(CancellationToken token)
    {
        if (!await Preparation.PrepareAsync(Installation.Current!, token)
            || !await machine.RestoreAsync(previous.Options, token))
        {
            throw new InvalidOperationException("TestManagedRestartPreparationRejected");
        }

        wal = new(new WriteAheadLog.Options { Location = Path.Combine(previous.Options.StoragePath, "wal"),
            FlushInterval = TimeSpan.Zero, FlushOnCommit = true, HashAlgorithm = WriteAheadLog.IntegrityHashAlgorithm.Crc64 }, machine);
        await wal.InitializeAsync(token);
        Native.Store = wal;
        Native.Replicate = async (entry, cancellation) =>
        {
            long index = await wal.AppendAsync(entry, cancellation);
            await wal.CommitAsync(index, cancellation);
            await wal.WaitForApplyAsync(index, cancellation);
            await RefreshAsync(cancellation);
        };
        await RefreshAsync(token);
        Preparation.PreparePeerAfterReplay(Installation.Current!);
    }

    public async Task RefreshAsync(CancellationToken token)
    {
        Recovery.VerifyNativeMembership(17, new HashSet<Guid> { previous.Options.NodeId }, false);
        bool replay = await Recovery.ConfirmLocalReplayAsync(wal!.LastCommittedEntryIndex, wal.LastAppliedIndex, 17, token);
        machine.ConfirmReplay(replay);
        foreach (string component in new[] { "Database", "Projection", "Storage", "Clock" })
        {
            Readiness.Report(component, true, "Ready", TimeSpan.FromMinutes(5));
        }

        Runtime.Publish(new(previous.Options.ClusterId, 1, previous.Options.NodeId, ((IPersistentState)wal).Term, 17,
            !Native.RemoteLeader, wal.LastCommittedEntryIndex, new HashSet<Guid> { previous.Options.NodeId }, CancellationToken.None, CancellationToken.None));
        Readiness.ConfirmRecovery(Readiness.RecoveryVersion, Runtime.Accepted, Runtime.Consensus!);
    }

    public async ValueTask DisposeAsync()
    {
        if (wal is not null)
        {
            await wal.DisposeAsync();
        }

        await services.DisposeAsync();
        Lease.Dispose();
    }

    private class ReadyRuntime(OnlineEnableState enable) : IInstallationRuntime
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
            if (!enable.Allows())
            {
                throw new InvalidOperationException("TestOnlineEnableMissing");
            }

            return Task.CompletedTask;
        }
    }

    private class NoBind : IRevisionTransport
    {
        public Task<IReadOnlyList<CommandResult>> SendAsync(IReadOnlyList<RevisionCommand> commands, CancellationToken token)
        {
            throw new InvalidOperationException("RestartMustNotBind");
        }
    }

    private class Projection(RevisionImage previous) : IRevisionProjectionStore
    {
        public Task ApplyAsync(ProjectionWriteSet changes, CancellationToken token)
        {
            return Task.CompletedTask;
        }

        public Task<ProjectionHead?> ReadHeadAsync(CancellationToken token)
        {
            return Task.FromResult<ProjectionHead?>(new(previous.ClusterId, previous.Generation, previous.Index, previous.Term, previous.Digest));
        }

        public Task VerifyAsync(RevisionImage image, CancellationToken token)
        {
            return Task.CompletedTask;
        }

        public Task InstallAsync(RevisionImage image, Guid snapshotId, string snapshotDigest, CancellationToken token)
        {
            throw new NotSupportedException();
        }

        public Task<RevisionImage> ExportAsync(RevisionImage image, CancellationToken token)
        {
            return Task.FromResult(image);
        }

        public Task CheckpointAsync(Guid snapshotId, string digest, long index, CancellationToken token)
        {
            return Task.CompletedTask;
        }
    }
}
