using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Cluster;

public class RevisionClusterRecovery(IRecoveryAdmission admissionSource, RevisionStateStore state,
    RevisionApplyEngine engine, IRevisionProjectionStore projection, RevisionSnapshotStore snapshots,
    IServiceScopeFactory scopes, IOptions<ClusterOptions> configured, IClusterLocalInitialization? initialization = null,
    ManagedStorageSeals? managedSeals = null, TimeProvider? time = null) : IClusterRecovery
{
    private RecoveryAdmission? admission;
    private bool replayConfirmed;
    private bool restored;
    private bool backupReconciled;
    private long provenAppliedIndex;
    private long membershipVersion = -1;
    private long verifiedNativeMembership = -1;
    private readonly SemaphoreSlim checkpointSerial = new(1, 1);
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
            || proof.NodeId != nodeId || proof.StorageGeneration != storageGeneration
            || !RecoveryProofValidation.CanRestore(proof, configured.Value.Compatibility != Onboarding.OnboardingProtocol.TRUST_COMPATIBILITY))
        {
            return false;
        }

        admission = proof;
        if (initialization is not null)
        {
            await initialization.InitializeAsync(proof, token);
        }

        if (proof.MonitoringNodes.Any(node => node.Key == Guid.Empty || node.Value == Guid.Empty))
        {
            throw new InvalidDataException("MonitoringAdmissionInvalid");
        }

        RevisionImage checkpoint = new(clusterId, generation)
        {
            Monitoring = new MonitoringImage { Nodes = proof.MonitoringNodes },
            Operations = proof.Operations
        };
        if (proof.Proof is FreshBootstrapProof genesis)
        {
            checkpoint = checkpoint with { OnboardingContracts = new OnboardingContractImage { Genesis = genesis.Authorization } };
        }
        if (proof.BootstrapAccount is not null)
        {
            RevisionCommand bootstrap = proof.BootstrapAccount;
            RevisionCodec.Validate(bootstrap);
            if (bootstrap.Action != "BootstrapAccount" || bootstrap.ClusterId != clusterId
                || bootstrap.ClusterGeneration != generation)
            {
                throw new InvalidDataException("BootstrapProvenanceMismatch");
            }

            checkpoint = checkpoint with { BootstrapAccountDigest = RevisionCodec.Digest(bootstrap) };
        }
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
            if (checkpoint.RecoveryBaseline is not null)
            {
                string manifestPath = Path.Combine(configured.Value.StoragePath, "manifest.json");
                ClusterDiskManifest? manifest = System.Text.Json.JsonSerializer.Deserialize<ClusterDiskManifest>(await File.ReadAllBytesAsync(manifestPath, token));
                if (manifest?.RecoveryId != checkpoint.RecoveryBaseline.RecoveryId
                    || checkpoint.RecoveryBaseline.PreviousClusterId == clusterId)
                {
                    throw new InvalidDataException("RecoveryBaselineProvenanceMismatch");
                }
            }
            if (!checkpoint.Monitoring.Nodes.OrderBy(node => node.Key).SequenceEqual(proof.MonitoringNodes.OrderBy(node => node.Key)))
            {
                throw new InvalidDataException("MonitoringAdmissionMismatch");
            }
            if (proof.BootstrapAccount is not null && checkpoint.BootstrapAccountDigest != RevisionCodec.Digest(proof.BootstrapAccount))
            {
                throw new InvalidDataException("BootstrapHistoryMismatch");
            }
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

                if (admission is null || !snapshot.Image.Monitoring.Nodes.OrderBy(node => node.Key)
                    .SequenceEqual(admission.MonitoringNodes.OrderBy(node => node.Key)))
                {
                    throw new InvalidDataException("MonitoringAdmissionMismatch");
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

            if (replayConfirmed && managedSeals?.Exists == true)
            {
                await CreateSnapshotAsync(token);
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
            || !MembershipMatches(persistedMembershipVersion))
        {
            state.Invalidate();
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

            await projection.VerifyAsync(image, token);
            if (admission.Proof is ManagedRestartProof)
            {
                OperationalNode? node = image.Operations.Nodes.GetValueOrDefault(admission.NodeId);
                if (node is null || node.Removed || !node.Voting || node.StorageGeneration != admission.StorageGeneration
                    || image.Operations.MaintenanceId != Guid.Empty)
                {
                    return false;
                }

                OperationalCredential? credential = image.Operations.Credentials.GetValueOrDefault(configured.Value.CredentialId);
                if (credential is null || credential.NodeId != node.NodeId
                    || !EnrollmentPeerTrustPolicy.IsActive(image, node, credential, (time ?? TimeProvider.System).GetUtcNow())
                    || credential.RevokedAtUtc.HasValue || (time ?? TimeProvider.System).GetUtcNow() >= credential.ExpiresAtUtc)
                {
                    return false;
                }
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

    private bool MembershipMatches(long persisted)
    {
        return persisted == admission!.MembershipVersion || persisted == verifiedNativeMembership;
    }

    public async Task<bool> VerifyCatchUpStartAsync(long committedIndex, long appliedIndex, long persistedMembershipVersion,
        CancellationToken token)
    {
        RevisionImage? image = state.Current;
        long version = state.RecoveryVersion;
        if (!restored || admission is null || admission.Proof is ManagedRestartProof || image is null || appliedIndex != committedIndex
            || image.Index > appliedIndex || committedIndex >= admission.MinimumCommittedIndex
            || !MembershipMatches(persistedMembershipVersion))
        {
            return false;
        }

        // До native Start нет concurrent apply. Проверка разрешает только догон,
        // не публикует аккаунты, readiness или рабочее право.
        await projection.VerifyAsync(image, token);
        return ReferenceEquals(image, state.Current) && version == state.RecoveryVersion;
    }

    public bool VerifyNativeMembership(long version, IReadOnlySet<Guid> voters, bool initialEmpty)
    {
        verifiedNativeMembership = -1;
        ClusterOperationsImage? operations = state.Current?.Operations;
        if (admission is null || operations is null || version < admission.MembershipVersion)
        {
            return false;
        }

        HashSet<Guid> expected = operations.Nodes.Values.Where(node => node.Voting && !node.Removed)
            .Select(node => node.NodeId).ToHashSet();
        if (admission.Proof is FreshBootstrapProof genesis && state.Current?.OnboardingContracts?.GenesisInitialized == false)
        {
            // Provisional identity разрешает только singleton native membership до accepted InitializeCluster.
            // Она не попадает в operational registry/peer allowlist и не создаёт account authority.
            expected = [genesis.Authorization.NodeId];
            bool singleton = expected.SetEquals(voters);
            if (singleton)
            {
                verifiedNativeMembership = version;
                return true;
            }
        }

        bool matches = expected.SetEquals(voters) && version == operations.MembershipVersion;
        if (initialEmpty && voters.Count == 0 && version == admission.MembershipVersion)
        {
            // До ColdStart/Join native storage пуст; это не подтверждает рабочий состав.
            return true;
        }

        MembershipOperation? pending = operations.Changes.Values.SingleOrDefault(change => !change.ConfirmedVersion.HasValue);
        if (pending is not null && version > pending.ExpectedVersion)
        {
            if (pending.Remove)
            {
                expected.Remove(pending.NodeId);
            }
            else
            {
                expected.Add(pending.NodeId);
            }

            matches = expected.SetEquals(voters);
        }

        if (matches)
        {
            verifiedNativeMembership = version;
        }

        return matches;
    }

    public Guid? ResolveNativeVoter(string address)
    {
        RevisionImage? image = state.Current;
        if (!restored || admission is null || image is null)
        {
            return null;
        }

        OperationalNode? node = image.Operations.Nodes.Values.SingleOrDefault(item =>
            item.Voting && !item.Removed && item.Address == address);
        if (node is not null)
        {
            return node.NodeId;
        }

        if (admission.Proof is FreshBootstrapProof genesis && image.OnboardingContracts?.GenesisInitialized == false
            && genesis.Authorization.PeerUri == address && image.Operations.Nodes.Count == 0)
        {
            return genesis.Authorization.NodeId;
        }

        return null;
    }

    private async Task ReconcileBackupAsync(RevisionImage image, CancellationToken token)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IPendingControlUnitOfWork work = scope.ServiceProvider.GetRequiredService<IPendingControlUnitOfWork>();
        await PendingCommandService.ReconcileAsync(work, image, token);
    }

    public async Task CreateSnapshotAsync(CancellationToken token)
    {
        await checkpointSerial.WaitAsync(token);
        try
        {
            if (!Accepted.CaughtUp)
            {
                throw new InvalidOperationException("CatchUpUnconfirmed");
            }

            RevisionImage image = await engine.CaptureAsync(token);
            PublishedRevisionSnapshot snapshot = await snapshots.PublishAsync(image, token);
            await projection.CheckpointAsync(snapshot.Manifest.SnapshotId, snapshot.Digest, image.Index, token);
            if (managedSeals is not null)
            {
                await managedSeals.WriteAsync(snapshot, token);
            }

            snapshots.ConfirmInstalled(snapshot);
        }
        finally
        {
            checkpointSerial.Release();
        }
    }

    public ValueTask ReclaimAsync(long watermark, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        snapshots.Reclaim(watermark);
        return ValueTask.CompletedTask;
    }
}
