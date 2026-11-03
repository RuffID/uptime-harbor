namespace UptimeHarbor.Application.Installation;

/// <summary>Разные proof для quarantined native start и общего допуска; реализует этап 04.</summary>
public interface IManagedRuntimeAdmission
{
    Task<bool> CanStartAsync(LocalInstallationProfile profile, CancellationToken token);
    Task<bool> CanWorkAsync(LocalInstallationProfile profile, CancellationToken token);
}
