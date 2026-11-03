using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Web.Cluster;

/// <summary>После native genesis/account принимает разные process/activation/enable операции; setup отзывается lifecycle.</summary>
public class ManagedBootstrapLifecycle(InstallationState installation, InstallationLease lease,
    ManagedAdmissionState admission, ILocalSetupUnitOfWork work, OnlineGrantSigner grants,
    OnlineProcessRenewalSigner renewal, OnlineEnableState enable, RevisionStateStore revisions,
    ClusterRuntimeState runtime, ClusterRaftHandle handle, NodeReadiness readiness,
    RevisionDispatcher dispatcher, InstallationLifecycle lifecycle, IOptions<ClusterOptions> options, TimeProvider time,
    RevisionClusterRecovery? recovery = null, ManagedStorageSeals? seals = null)
{
    public async Task TickAsync(CancellationToken token)
    {
        LocalInstallationProfile? profile = installation.Current;
        RecoveryAdmission? local = admission.Read();
        if (profile is null || profile.Mode != "Bootstrap" || local?.Proof is not (FreshBootstrapProof or ManagedRestartProof)
            || profile.Phase is not (InstallationPhase.ClusterQuarantine or InstallationPhase.CatchingUp
                or InstallationPhase.AwaitingActivation or InstallationPhase.ClusterReady) || !await BarrierAsync(token))
        {
            return;
        }

        RevisionImage image = revisions.Current!;
        if (!runtime.Accepted.AccountStateConfirmed || image.OnboardingContracts?.GenesisInitialized != true)
        {
            return;
        }

        if (profile.Phase == InstallationPhase.ClusterQuarantine)
        {
            await lifecycle.AdvanceAsync(profile.ProfileRevision, profile.RuntimeGeneration, InstallationPhase.CatchingUp, null, token);
            return;
        }

        if (profile.Phase == InstallationPhase.CatchingUp)
        {
            await lifecycle.AdvanceAsync(profile.ProfileRevision, profile.RuntimeGeneration, InstallationPhase.AwaitingActivation, null, token);
            return;
        }

        OnlineProcessLease? process = image.OnboardingContracts.OnlineProcesses?.GetValueOrDefault(profile.NodeId!.Value);
        if (process is null || process.BootId != lease.BootId || process.BootChallenge != lease.BootChallenge
            || process.RecoveryVersion != readiness.RecoveryVersion || time.GetUtcNow() >= process.LeaseUntilUtc)
        {
            await SubmitAsync(OnlineGrantPurpose.Process, "RegisterOnlineProcess", token);
            return;
        }

        if (process.LeaseUntilUtc - time.GetUtcNow() <= TimeSpan.FromMinutes(1))
        {
            await SubmitAsync(OnlineGrantPurpose.Process, "RenewOnlineProcess", token);
            return;
        }

        if (!image.Operations.Nodes[profile.NodeId!.Value].Activated)
        {
            await SubmitAsync(OnlineGrantPurpose.Activation, "AuthorizeOnboardingActivation", token);
            return;
        }

        if (profile.Phase == InstallationPhase.ClusterReady && enable.Allows())
        {
            return;
        }

        OnlineGrant? enabled = await SubmitAsync(OnlineGrantPurpose.Enable, "AuthorizeOnlineEnable", token);
        if (enabled is null || !await BarrierAsync(token) || !enable.Confirm(enabled))
        {
            return;
        }

        profile = installation.Current!;
        if (profile.Phase == InstallationPhase.AwaitingActivation)
        {
            if (seals is not null && !seals.Exists)
            {
                await (recovery ?? throw new InvalidOperationException("ManagedCheckpointWriterRequired")).CreateSnapshotAsync(token);
                profile = installation.Current!;
            }

            SetupAuthorityEvidence evidence = new(profile.AttemptId!.Value, profile.RuntimeGeneration,
                enable.AcceptedIndex, enabled.RequestDigest, SetupAuthorityEvidenceKind.ActivationVerified);
            await lifecycle.AdvanceAsync(profile.ProfileRevision, profile.RuntimeGeneration, InstallationPhase.ClusterReady, evidence, token);
        }
    }

    private async Task<OnlineGrant?> SubmitAsync(OnlineGrantPurpose purpose, string action, CancellationToken token)
    {
        RevisionCommand command = await PrepareAsync(purpose, action, token);
        CommandResult result = await dispatcher.AcceptAsync(command, options.Value.NodeId, token);
        if (result.Outcome != OperationOutcome.Committed)
        {
            return null;
        }

        if (purpose == OnlineGrantPurpose.Process)
        {
            return command.OnlineLifecycle!.Grant;
        }

        string consume = "ConsumeOnboardingActivation";
        if (purpose == OnlineGrantPurpose.Enable)
        {
            consume = "EnableOnlineGrant";
        }

        RevisionCommand consumption = command with
        {
            OperationId = OnlineLifecycleCommands.ConsumeId(command.OperationId), Action = consume
        };
        CommandResult consumed = await dispatcher.AcceptAsync(consumption, options.Value.NodeId, token);
        if (consumed.Outcome != OperationOutcome.Committed)
        {
            return null;
        }

        return command.OnlineLifecycle!.Grant;
    }

    private async Task<RevisionCommand> PrepareAsync(OnlineGrantPurpose purpose, string action, CancellationToken token)
    {
        await installation.Transition.WaitAsync(token);
        try
        {
            LocalInstallationProfile profile = await work.Profiles.ReadAsync(token)
                ?? throw new InvalidDataException("InstallationProfileMissing");
            if (!LocalProfileEquality.Same(profile, installation.Current) || profile.BootId != lease.BootId)
            {
                throw new InvalidDataException("OnlineLifecycleProcessChanged");
            }

            RevisionImage image = revisions.Current ?? throw new InvalidOperationException("ProjectionUnavailable");
            if (admission.Read()?.Proof is not (FreshBootstrapProof or ManagedRestartProof))
            {
                throw new UnauthorizedAccessException("OnlineProcessUnconfirmed");
            }
            OnlineProcessLease? process = image.OnboardingContracts!.OnlineProcesses?.GetValueOrDefault(profile.NodeId!.Value);
            Guid predecessor = Guid.Empty;
            if (purpose == OnlineGrantPurpose.Process && process is not null)
            {
                predecessor = process.GrantId;
            }

            string identity = $"UptimeHarbor.ManagedLifecycle.v1/{profile.AttemptId:D}/{lease.BootId:D}/{lease.BootChallenge}/{action}/{readiness.RecoveryVersion}/{image.Operations.Revision}/{image.Operations.MembershipVersion}/{predecessor:D}";
            Guid operationId = new(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
            while (true)
            {
                CommandResult? previous = image.Results.GetValueOrDefault(operationId);
                if (previous is not null && previous.Outcome != OperationOutcome.Committed)
                {
                    operationId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{identity}/{previous.Index}")).AsSpan(0, 16));
                    continue;
                }

                LocalAttemptStep? saved = await work.Profiles.FindStepAsync(operationId, token);
                if (saved is null)
                {
                    break;
                }

                RevisionCommand original = JsonSerializer.Deserialize<RevisionCommand>(saved.Evidence
                    ?? throw new InvalidDataException("OnlineLifecycleIntentMissing"))!;
                if (saved.AttemptId != profile.AttemptId || saved.Kind != action || saved.RuntimeGeneration != profile.RuntimeGeneration
                    || original.OperationId != operationId || saved.RequestDigest != RevisionCodec.Digest(original))
                {
                    throw new InvalidDataException("OnlineLifecycleIntentChanged");
                }

                CommandResult? consumed = image.Results.GetValueOrDefault(OnlineLifecycleCommands.ConsumeId(operationId));
                if (time.GetUtcNow() >= original.OnlineLifecycle!.Grant.ExpiresAtUtc
                    && consumed?.Outcome != OperationOutcome.Committed)
                {
                    // Tick уже подтвердил свежий majority cut. Прежние intent/results сохраняются;
                    // истёкший grant не может позднее предоставить новые полномочия.
                    operationId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{identity}/expired/{operationId:D}")).AsSpan(0, 16));
                    continue;
                }

                return original;
            }

            PreparedGenesis prepared = JsonSerializer.Deserialize<SetupAuthorityEvidence>(profile.Evidence!)!.Genesis!;
            RecoveryAdmission local = admission.Read()!;
            Guid actorId = local.Proof is ManagedRestartProof restart ? restart.ActorId : local.BootstrapAccount!.ActorUserId;
            AcceptedValue actor = image.Values[actorId];
            long processEpoch = process?.ProcessEpoch ?? 1;
            if (process is not null && process.BootId != lease.BootId)
            {
                processEpoch++;
            }

            DateTimeOffset now = time.GetUtcNow();
            OnlineGrant request = new(1, purpose, image.ClusterId, image.Generation, profile.AttemptId!.Value,
                null, null, profile.NodeId!.Value, profile.StorageGeneration!.Value, profile.PeerAddress!, prepared.Request.SpkiDigest,
                profile.CredentialId!.Value, prepared.InitializeCluster.EnrollmentTrust!.Credential!.Fingerprint,
                prepared.Template.IssuerEpoch, operationId, profile.BootstrapAccountDigest!, lease.BootId, lease.BootChallenge,
                processEpoch, actor.Id, actor.Revision, 0, readiness.RecoveryVersion, image.Operations.Revision,
                image.Operations.MembershipVersion, image.Index, null, now, now.AddMinutes(2), string.Empty);
            OnlineGrant signed;
            if (action == "RenewOnlineProcess")
            {
                signed = await renewal.SignAsync(request, token);
            }
            else
            {
                signed = await grants.SignAsync(request, token);
            }

            RevisionCommand command = new()
            {
                Schema = 3, ClusterId = image.ClusterId, ClusterGeneration = image.Generation,
                OperationId = operationId, OriginNodeId = profile.NodeId.Value, ActorUserId = Guid.Empty, ActorRevision = 0,
                EntityId = profile.NodeId.Value, EntityKind = DeletedEntityKind.Node, Action = action,
                BaseRevision = signed.OperationsRevision, CreatedAtUtc = now, SystemSource = "OnlineLifecycle",
                OnlineLifecycle = new(1, profile.StorageGeneration.Value, signed)
            };
            await work.ExecuteInTransactionAsync(_ =>
            {
                work.Profiles.AddStep(new(operationId, profile.AttemptId.Value, action, RevisionCodec.Digest(command),
                    "AcceptedOnlineLifecycle", profile.ProfileRevision, profile.RuntimeGeneration, now, JsonSerializer.Serialize(command)));
                return Task.CompletedTask;
            }, token);
            return command;
        }
        finally
        {
            installation.Transition.Release();
        }
    }

    private async Task<bool> BarrierAsync(CancellationToken token)
    {
        var raft = handle.Current;
        ConsensusStatus? before = runtime.Consensus;
        if (raft is null || before is null || !before.IsLeader || raft.Leader?.IsRemote != false || !revisions.Available
            || !runtime.Accepted.CaughtUp || !runtime.Accepted.SnapshotConfirmed || !Healthy())
        {
            return false;
        }

        using CancellationTokenSource guard = CancellationTokenSource.CreateLinkedTokenSource(token, raft.LeadershipToken, raft.ConsensusToken);
        await raft.ApplyReadBarrierAsync(ReadBarrierType.Strong, guard.Token);
        guard.Token.ThrowIfCancellationRequested();
        ConsensusStatus? after = runtime.Consensus;
        return handle.Current == raft && after is not null && after.IsLeader && after.Term == before.Term
            && after.MembershipVersion == before.MembershipVersion && after.Term == raft.AuditTrail.Term
            && runtime.Accepted.CaughtUp && runtime.Accepted.AppliedIndex >= raft.AuditTrail.LastCommittedEntryIndex
            && runtime.Accepted.MembershipVersion == after.MembershipVersion && Healthy();
    }

    private bool Healthy()
    {
        return new[] { "Database", "Projection", "Storage", "Clock" }.All(name => readiness.Get(name).Status == "Ready");
    }
}
