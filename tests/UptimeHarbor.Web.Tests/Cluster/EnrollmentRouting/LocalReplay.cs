using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

internal sealed class LocalReplay : IDisposable
{
    internal InstallationState Installation { get; } = new();
    internal RevisionStateStore State { get; } = new();
    internal RevisionClusterRecovery Recovery { get; }
    internal ManagedContinuationReplay Evidence { get; private set; } = null!;
    private readonly ServiceProvider services = new ServiceCollection().BuildServiceProvider();
    private readonly Projection projection;
    private readonly RecoveryAdmission admission;

    private LocalReplay(RevisionImage image, VoterContinuationClient.Intent intent)
    {
        OperationalNode node = image.Operations.Nodes[intent.Request.Renewal.Issuance.NodeId];
        Guid identity = Guid.NewGuid();
        LocalInstallationProfile profile = new(identity, "Bootstrap", InstallationPhase.ClusterQuarantine,
            1, 1, 1, intent.Process.BootId, DateTimeOffset.UnixEpoch, NodeId: node.NodeId,
            StorageGeneration: node.StorageGeneration, PeerAddress: node.Address,
            ClusterId: image.ClusterId, ClusterGeneration: image.Generation) { ManagedRestart = true };
        Installation.Publish(profile);
        admission = new(image.ClusterId, image.Generation, node.NodeId, node.StorageGeneration,
            false, false, null, null, image.Index, image.Operations.MembershipVersion)
        {
            Operations = image.Operations,
            Proof = new ManagedRestartProof(identity, intent.Process.BootId, intent.Process.BootChallenge, new string('A', 64))
        };
        var options = Options.Create(new ClusterOptions
        {
            Mode = "Restart", ClusterId = image.ClusterId, Generation = image.Generation, NodeId = node.NodeId,
            StorageGeneration = node.StorageGeneration, PublicUri = node.Address, ProtocolMajor = 3,
            CommandSchema = 3, SnapshotFormat = 3, SqlSchema = 3
        });
        projection = new(image);
        Recovery = new(new Admission(admission), State, new(State, new RevisionReducer(), projection),
            projection, new(options), services.GetRequiredService<IServiceScopeFactory>(), options);
    }

    internal static async Task<LocalReplay> OpenAsync(VoterContinuationHttpAuthority authority,
        VoterContinuationClient.Intent intent, CancellationToken token)
    {
        RevisionImage image = authority.ReadImage();
        LocalReplay local = new(image, intent);
        await local.Installation.Transition.WaitAsync(token);
        try
        {
            RecoveryAdmission proof = local.admission;
            Assert.True(await local.Recovery.RestoreAsync(proof.ClusterId, proof.Generation, proof.NodeId, proof.StorageGeneration, token));
            // Feed the synthetic accepted replay through the isolated projection port. Native Raft stays stopped.
            local.State.Publish(image);
            Assert.True(local.Recovery.VerifyNativeMembership(proof.MembershipVersion, image.Operations.Nodes.Keys.ToHashSet(), false));
            local.Evidence = await local.Recovery.VerifyManagedContinuationReplayAsync(local.Installation,
                image.Index, image.Index, image.Term, proof.MembershipVersion, token);
            Assert.Equal(1, local.projection.Verifications);
            return local;
        }
        catch
        {
            local.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Installation.Transition.Release();
        Installation.Transition.Dispose();
        services.Dispose();
    }

    private sealed class Admission(RecoveryAdmission proof) : IRecoveryAdmission
    {
        public ValueTask<RecoveryAdmission?> ReadAsync(CancellationToken token) => ValueTask.FromResult<RecoveryAdmission?>(proof);
    }

    private sealed class Projection(RevisionImage replay) : IRevisionProjectionStore
    {
        internal int Verifications { get; private set; }

        public Task VerifyAsync(RevisionImage image, CancellationToken token)
        {
            Assert.Same(replay, image);
            Verifications++;
            return Task.CompletedTask;
        }

        public Task<ProjectionHead?> ReadHeadAsync(CancellationToken token) =>
            Task.FromResult<ProjectionHead?>(VoterContinuationSlice.Head(replay));

        public Task ApplyAsync(ProjectionWriteSet changes, CancellationToken token) => throw new InvalidOperationException();
        public Task InstallAsync(RevisionImage image, Guid snapshotId, string snapshotDigest, CancellationToken token) =>
            throw new InvalidOperationException();
        public Task<RevisionImage> ExportAsync(RevisionImage image, CancellationToken token) => throw new InvalidOperationException();
        public Task CheckpointAsync(Guid snapshotId, string digest, long index, CancellationToken token) => throw new InvalidOperationException();
    }
}
