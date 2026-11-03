namespace UptimeHarbor.Application.Installation;

public interface IInstallationRuntime
{
    Task<bool> DrainAsync(long generation, TimeSpan budget, CancellationToken token);
    Task StartQuarantinedAsync(LocalInstallationProfile profile, CancellationToken token);
    Task PublishReadyAsync(LocalInstallationProfile profile, CancellationToken token);
}
