namespace UptimeHarbor.Application.Installation;

public interface ILocalInstallationRepository
{
    Task<LocalInstallationProfile?> ReadAsync(CancellationToken token);
    void Create(LocalInstallationProfile profile);
    Task ReplaceAsync(LocalInstallationProfile profile, long expectedRevision, CancellationToken token);
    Task<LocalAttemptStep?> FindStepAsync(Guid operationId, CancellationToken token);
    void AddStep(LocalAttemptStep step);
}
