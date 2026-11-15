using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;

/// <summary>Remote request не ослабляет прежнее доказательство локального Bootstrap/ManagedRestart процесса.</summary>
public static class OnlineGrantLocalSource
{
    public static void Require(OnlineGrant grant, ClusterOptions options, NodeReadiness readiness,
        ManagedAdmissionState? admission, InstallationLease? lease)
    {
        RecoveryAdmission? local = admission?.Read();
        bool process = false;
        if (lease is not null)
        {
            if (local?.Proof is FreshBootstrapProof bootstrap)
            {
                process = bootstrap.BootId == lease.BootId && grant.AttemptId == bootstrap.Authorization.GenesisId;
            }
            else if (local?.Proof is ManagedRestartProof restart)
            {
                process = restart.InstallIdentity == lease.InstallIdentity && restart.BootId == lease.BootId
                    && restart.BootChallenge == lease.BootChallenge;
            }
        }

        if (!process || lease is null || grant.BootId != lease.BootId || grant.BootChallenge != lease.BootChallenge
            || grant.NodeId != options.NodeId || grant.StorageGeneration != options.StorageGeneration
            || grant.RecoveryVersion != readiness.RecoveryVersion)
        {
            throw new UnauthorizedAccessException("OnlineGrantProcessUnconfirmed");
        }
    }
}
