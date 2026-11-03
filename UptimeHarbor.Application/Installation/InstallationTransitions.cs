namespace UptimeHarbor.Application.Installation;

public static class InstallationTransitions
{
    public static bool Allowed(InstallationPhase from, InstallationPhase to)
    {
        if (from == InstallationPhase.ClusterReady)
        {
            return false;
        }

        if (to is InstallationPhase.FailedNeedsAction or InstallationPhase.Paused)
        {
            return true;
        }

        return (from, to) switch
        {
            (InstallationPhase.SetupReady, InstallationPhase.Preparing) => true,
            (InstallationPhase.StandaloneReady, InstallationPhase.Preparing) => true,
            (InstallationPhase.Preparing, InstallationPhase.Quiescing) => true,
            (InstallationPhase.Quiescing, InstallationPhase.LocalPrepared) => true,
            (InstallationPhase.LocalPrepared, InstallationPhase.ClusterQuarantine) => true,
            (InstallationPhase.ClusterQuarantine, InstallationPhase.CatchingUp) => true,
            (InstallationPhase.CatchingUp, InstallationPhase.AwaitingActivation) => true,
            (InstallationPhase.AwaitingActivation, InstallationPhase.ClusterReady) => true,
            _ => false
        };
    }

    public static bool SetupOpen(InstallationPhase phase)
    {
        return phase is not (InstallationPhase.ClusterReady or InstallationPhase.StandaloneReady);
    }
}
