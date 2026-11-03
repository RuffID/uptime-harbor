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

namespace UptimeHarbor.Tests.Web.Cluster;

/// <summary>Новый process source и повторное открытие настоящего WAL; SQL/majority остаются doubles, native Start не вызывается.</summary>
public class ManagedRestartFixture : IAsyncDisposable
{
    public InstallationLease Lease { get; }
    public InstallationState Installation { get; } = new();
    public RevisionStateStore Accepted { get; } = new();
    public ManagedAdmissionState Admission { get; }
    public ManagedStorageSeals Seals { get; }
    public ManagedRestartPreparation Preparation { get; }
    public NodeReadiness Readiness { get; }
    public ClusterRuntimeState Runtime { get; }
    public ManagedRaftProbe Native { get; }
    public OnlineEnableState Enable { get; }
    public ManagedBootstrapLifecycle Driver { get; }
    public RevisionClusterRecovery Recovery { get; }
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
        Enable = new(Accepted, Installation, Lease, Readiness, previous.Time);
        ClusterLocalControl control = new(configured, Options.Create(new ClusterControlOptions()), previous.Time, Readiness, Admission, Enable);
        ManagedRecoveryAdmission source = new(local, Admission, new(control, configured, previous.Time));
        Projection projection = new(previous.Accepted.Current!);
        RevisionSnapshotStore snapshots = new(configured);
        DynamicPeerTls tls = new(Installation, Accepted, previous.Time);
        Preparation = new(Seals, snapshots, projection, Installation, Lease, secrets, Admission, tls, configured, previous.Time);
        Recovery = new(source, Accepted, new(Accepted, new(), projection), projection, snapshots,
            services.GetRequiredService<IServiceScopeFactory>(), configured, managedSeals: Seals, time: previous.Time);
        machine = new(Recovery, Readiness);
        Runtime = new(Recovery, Accepted, control, configured, previous.Time);
        IRaftCluster raft = DispatchProxy.Create<IRaftCluster, ManagedRaftProbe>();
        Native = (ManagedRaftProbe)raft;
        ClusterRaftHandle handle = new();
        typeof(ClusterRaftHandle).GetProperty(nameof(ClusterRaftHandle.Current))!.SetValue(handle, raft);
        GenesisWriterPermit permit = new();
        ClusterConsensusWriter writer = new(handle, Runtime, Readiness, configured, Accepted, permit);
        OnboardingAcceptance acceptance = new(handle, Runtime, Readiness, configured, new EnrollmentAuthority(), previous.Time, Accepted, permit);
        RevisionDispatcher dispatcher = new(writer, Accepted, Runtime, time: previous.Time, onboarding: acceptance);
        OnlineGrantSigningAuthorization authority = new(handle, Runtime, Accepted, Readiness, configured, Admission, Lease, previous.Time);
        SetupAuthority setup = new(new(previous.Store, previous.Store, secrets, Lease, local, previous.Time), Enable);
        InstallationLifecycle lifecycle = new(previous.Store, previous.Store, Installation, new ReadyRuntime(Enable), setup,
            Lease, previous.Time, NullLogger<InstallationLifecycle>.Instance);
        Driver = new(Installation, Lease, Admission, previous.Store, new(previous.Store, authority, new(), secrets, Lease, previous.Time),
            new(previous.Store, Installation, Lease, Accepted, secrets, previous.Time), Enable, Accepted, Runtime, handle, Readiness,
            dispatcher, lifecycle, configured, previous.Time, Recovery, Seals);
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
            true, wal.LastCommittedEntryIndex, new HashSet<Guid> { previous.Options.NodeId }, CancellationToken.None, CancellationToken.None));
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
