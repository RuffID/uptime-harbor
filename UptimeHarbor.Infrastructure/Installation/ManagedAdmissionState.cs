using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Process-local quarantined authority. Его публикует только проверенная подготовка; общего CanWork здесь нет.</summary>
public class ManagedAdmissionState(InstallationState installation, IOptions<InstallationOptions> local,
    IOptions<ClusterControlOptions> control, InstallationLease lease)
{
    private RecoveryAdmission? current;
    private long generation;

    internal void Publish(LocalInstallationProfile profile, RecoveryAdmission proof)
    {
        bool process = proof.Proof switch
        {
            FreshBootstrapProof bootstrap => bootstrap.BootId == lease.BootId && bootstrap.BootChallenge == lease.BootChallenge,
            ManagedRestartProof restart => profile.ManagedRestart && restart.InstallIdentity == lease.InstallIdentity
                && restart.BootId == lease.BootId && restart.BootChallenge == lease.BootChallenge,
            _ => false
        };
        if (profile != installation.Current || profile.BootId != lease.BootId || !process)
        {
            throw new InvalidOperationException("ManagedAdmissionProcessMismatch");
        }

        generation = profile.RuntimeGeneration;
        Volatile.Write(ref current, proof);
    }

    public RecoveryAdmission? Read()
    {
        LocalInstallationProfile? profile = installation.Current;
        RecoveryAdmission? proof = Volatile.Read(ref current);
        if (profile is null || profile.BootId != lease.BootId || profile.RuntimeGeneration != generation
            || profile.Phase is not (InstallationPhase.ClusterQuarantine or InstallationPhase.CatchingUp
                or InstallationPhase.AwaitingActivation or InstallationPhase.ClusterReady))
        {
            return null;
        }

        return proof;
    }

    public bool AllowsConsensus(Guid maintenance)
    {
        if (!local.Value.Setup || Read() is null || maintenance != Guid.Empty)
        {
            return false;
        }

        foreach (string directory in new[] { local.Value.StateDirectory, control.Value.Directory })
        {
            if (directory.Length > 0 && (File.Exists(Path.Combine(directory, "blocked"))
                || File.Exists(Path.Combine(directory, "maintenance"))))
            {
                return false;
            }
        }

        return true;
    }
}
