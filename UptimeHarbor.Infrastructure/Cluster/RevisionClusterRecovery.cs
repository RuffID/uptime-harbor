using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster;

public class RevisionClusterRecovery(IRecoveryAdmission admissionSource, RevisionStateStore state,
    RevisionApplyEngine engine, IRevisionProjectionStore projection, RevisionSnapshotStore snapshots,
    IServiceScopeFactory scopes, IOptions<ClusterOptions> configured) : IClusterRecovery
{
    private RecoveryAdmission? admission;
    private bool replayConfirmed;
    private bool restored;
    private bool backupReconciled;
    private long provenAppliedIndex;
    private long membershipVersion = -1;
    public ClusterSnapshot? Snapshot => snapshots.Current?.AsRaftSnapshot();

    public AcceptedClusterState Accepted
    {
        get
        {
            RevisionImage? image = state.Current;
            bool confirmed = restored && replayConfirmed && state.Available && image is not null;
            bool accounts = confirmed && image!.Values.Values.Any(cell => cell.Kind == Domain.Cluster.DeletedEntityKind.Account
                && cell.Value.Enabled && !cell.Deleted);
            return new AcceptedClusterState(accounts, confirmed, confirmed, false, false,
                Math.Max(image?.Index ?? 0, provenAppliedIndex), membershipVersion, Guid.Empty, false);
        }
    }

    public async ValueTask<bool> RestoreAsync(Guid clusterId, long generation, Guid nodeId,
        Guid storageGeneration, CancellationToken token)
    {
        restored = false;
        replayConfirmed = false;
        backupReconciled = false;
        provenAppliedIndex = 0;
        state.BeginRecovery();
        RecoveryAdmission? proof = await admissionSource.ReadAsync(token);
        if (proof is null || proof.ClusterId != clusterId || proof.Generation != generation
            || proof.NodeId != nodeId || proof.StorageGeneration != storageGeneration || !proof.FencingConfirmed)
        {
            return false;
        }

        admission = proof;
        RevisionImage checkpoint = new(clusterId, generation);
        ProjectionHead? sql = await projection.ReadHeadAsync(token);
        if (sql is not null && (sql.ClusterId != clusterId || sql.Generation != generation))
        {
            throw new InvalidDataException("RecoveryIdentityMismatch");
        }

        if (proof.SnapshotId.HasValue)
        {
            if (proof.SnapshotDigest is null)
            {
                throw new InvalidDataException("SnapshotProvenanceMissing");
            }

            PublishedRevisionSnapshot snapshot = await snapshots.LoadAsync(proof.SnapshotId.Value, proof.SnapshotDigest, token);
            checkpoint = snapshot.Image;
            if (sql is null || sql.Index < checkpoint.Index)
            {
                await engine.InstallAsync(checkpoint, snapshot.Manifest.SnapshotId, snapshot.Digest, token);
            }
        }
        else if (proof.OldBackup)
        {
            throw new InvalidDataException("RestoredBackupRequiresSnapshot");
        }

        state.Publish(checkpoint with { ProjectionRows = [] });
        state.Invalidate();
        restored = true;
        return true;
    }

    public async ValueTask<long> ApplyAsync(CommittedRecord entry, CancellationToken token)
    {
        if (!restored)
        {
            throw new InvalidOperationException("RecoveryUnconfirmed");
        }

        try
        {
            if (entry.IsSnapshot)
            {
                PublishedRevisionSnapshot snapshot = await snapshots.ImportAsync(entry.CopyPayloadAsync, token);
                if (snapshot.Manifest.Index != entry.Index || snapshot.Manifest.Term != entry.Term)
                {
                    throw new InvalidDataException("RaftSnapshotCutMismatch");
                }

                await engine.InstallAsync(snapshot.Image, snapshot.Manifest.SnapshotId, snapshot.Digest, token);
                snapshots.ConfirmInstalled(snapshot);
            }
            else if (entry.IsConfiguration)
            {
                await engine.ApplyConfigurationAsync(entry.Index, entry.Term, configured.Value.NodeId, token);
            }
            else
            {
                await using LimitedMemoryStream bytes = new(configured.Value.CommandMaxBytes);
                await entry.CopyPayloadAsync(bytes, token);
                await engine.ApplyAsync(RevisionCodec.Decode(bytes.ToArray()), entry.Index, entry.Term,
                    configured.Value.NodeId, token);
            }

            return entry.Index;
        }
        catch
        {
            restored = false;
            replayConfirmed = false;
            state.Invalidate();
            throw;
        }
    }

    public async ValueTask<bool> ConfirmLocalReplayAsync(long committedIndex, long appliedIndex,
        long persistedMembershipVersion, CancellationToken token)
    {
        replayConfirmed = false;
        if (!restored || admission is null || committedIndex < admission.MinimumCommittedIndex
            || appliedIndex != committedIndex
            || persistedMembershipVersion != admission.MembershipVersion)
        {
            return false;
        }

        bool verified = await engine.ConfirmRecoveryAsync(async image =>
        {
            if (image.Index > appliedIndex)
            {
                return false;
            }

            // Payloadless no-op индексы DotNext не вызывают reducer.
            ProjectionHead? sql = await projection.ReadHeadAsync(token);
            if (sql is null && image.Index != 0)
            {
                return false;
            }

            if (sql is not null)
            {
                await projection.VerifyAsync(image, token);
            }

            if (admission.OldBackup && !backupReconciled)
            {
                await ReconcileBackupAsync(image, token);
                backupReconciled = true;
            }

            return true;
        }, token);
        if (!verified)
        {
            return false;
        }

        membershipVersion = persistedMembershipVersion;
        provenAppliedIndex = appliedIndex;
        replayConfirmed = true;
        return true;
    }

    private async Task ReconcileBackupAsync(RevisionImage image, CancellationToken token)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IPendingControlUnitOfWork work = scope.ServiceProvider.GetRequiredService<IPendingControlUnitOfWork>();
        await PendingCommandService.ReconcileAsync(work, image, token);
    }

    public async Task CreateSnapshotAsync(CancellationToken token)
    {
        if (!Accepted.CaughtUp)
        {
            throw new InvalidOperationException("CatchUpUnconfirmed");
        }

        RevisionImage image = await engine.CaptureAsync(token);
        PublishedRevisionSnapshot snapshot = await snapshots.PublishAsync(image, token);
        await projection.CheckpointAsync(snapshot.Manifest.SnapshotId, snapshot.Digest, image.Index, token);
        snapshots.ConfirmInstalled(snapshot);
    }

    public ValueTask ReclaimAsync(long watermark, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        snapshots.Reclaim(watermark);
        return ValueTask.CompletedTask;
    }
}
