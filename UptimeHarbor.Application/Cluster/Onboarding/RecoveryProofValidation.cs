using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster.Onboarding;

/// <summary>Структурная граница typed proof. Подпись/диск/accepted registry проверяет соответствующий admission source.</summary>
public static class RecoveryProofValidation
{
    public static bool CanRestore(RecoveryAdmission admission, bool legacyOffline = false)
    {
        switch (admission.Proof)
        {
            case null:
                return legacyOffline && admission.FencingConfirmed;
            case SignedOfflineRecoveryProof offline:
                return admission.FencingConfirmed && offline.IsolationEvidenceDigest.Length == 64
                    && offline.IsolationEvidenceDigest.All(char.IsAsciiHexDigit);
            case FreshBootstrapProof bootstrap:
                return ExactGenesis(admission, bootstrap);
            case FreshJoinProof join:
                return !admission.FencingConfirmed && !admission.OldBackup && admission.BootstrapAccount is null
                    && admission.SnapshotId.HasValue && admission.MinimumCommittedIndex >= join.AdmissionIndex
                    && join.AdmissionIndex > 0 && Process(join.AttemptId, join.GrantId, join.BootId, join.BootChallenge, join.ProcessEpoch);
            case OnboardingResumeProof resume:
                return !admission.FencingConfirmed && !admission.OldBackup && admission.BootstrapAccount is null
                    && resume.ResumeIndex > 0
                    && Process(resume.AttemptId, resume.GrantId, resume.BootId, resume.BootChallenge, resume.ProcessEpoch);
            case ManagedRestartProof restart:
                return !admission.FencingConfirmed && !admission.OldBackup && admission.BootstrapAccount is null
                    && restart.InstallIdentity != Guid.Empty && restart.BootId != Guid.Empty
                    && OnboardingCommands.IsBootChallenge(restart.BootChallenge)
                    && restart.SealedStorageDigest.Length == 64 && restart.SealedStorageDigest.All(char.IsAsciiHexDigit);
            default:
                return false;
        }
    }

    public static bool CanBootstrap(RecoveryAdmission admission, bool legacyOffline = false)
    {
        if (admission.Proof is null)
        {
            return legacyOffline && admission.FencingConfirmed && !admission.OldBackup;
        }

        if (admission.Proof is SignedOfflineRecoveryProof)
        {
            return CanRestore(admission) && !admission.OldBackup;
        }

        return admission.Proof is FreshBootstrapProof bootstrap && ExactGenesis(admission, bootstrap);
    }

    private static bool ExactGenesis(RecoveryAdmission admission, FreshBootstrapProof proof)
    {
        GenesisAuthorization genesis = proof.Authorization;
        RevisionCommand? account = admission.BootstrapAccount;
        return !admission.FencingConfirmed && !admission.OldBackup && !admission.SnapshotId.HasValue
            && admission.MinimumCommittedIndex == 0 && proof.BootId != Guid.Empty
            && admission.Operations.Nodes.IsEmpty && admission.Operations.Credentials.IsEmpty
            && admission.Operations.Changes.IsEmpty && admission.Operations.Sender is null
            && admission.Operations.MaintenanceId == Guid.Empty && admission.MonitoringNodes.IsEmpty
            && OnboardingCommands.IsBootChallenge(proof.BootChallenge) && genesis.MigrationId is null
            && genesis.ClusterId == admission.ClusterId && genesis.Generation == admission.Generation
            && genesis.NodeId == admission.NodeId && genesis.StorageGeneration == admission.StorageGeneration
            && proof.InitializeCluster.Action == "InitializeCluster"
            && proof.InitializeCluster.OperationId == genesis.GenesisId
            && proof.InitializeCluster.EnrollmentTrust?.Genesis == genesis
            && account is not null && account.Action == "BootstrapAccount"
            && account.ClusterId == genesis.ClusterId && account.ClusterGeneration == genesis.Generation
            && account.OriginNodeId == genesis.NodeId && RevisionCodec.Digest(account) == genesis.AccountOrImportDigest;
    }

    private static bool Process(Guid attempt, Guid grant, Guid boot, string challenge, long epoch)
    {
        return attempt != Guid.Empty && grant != Guid.Empty && boot != Guid.Empty
            && OnboardingCommands.IsBootChallenge(challenge) && epoch > 0;
    }
}
