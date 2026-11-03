namespace UptimeHarbor.Application.Installation;

public enum InstallationPhase
{
    SetupReady,
    StandaloneReady,
    Preparing,
    Quiescing,
    LocalPrepared,
    ClusterQuarantine,
    CatchingUp,
    AwaitingActivation,
    ClusterReady,
    Paused,
    FailedNeedsAction
}
