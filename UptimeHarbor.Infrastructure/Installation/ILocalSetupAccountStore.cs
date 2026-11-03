namespace UptimeHarbor.Infrastructure.Installation;

public interface ILocalSetupAccountStore
{
    Task<LocalSetupAccount?> ReadAsync(CancellationToken token);
    void Create(LocalSetupAccount account);
    Task<bool> HasProgressAsync(CancellationToken token);
    Task ValidateProgressAsync(UptimeHarbor.Application.Installation.LocalInstallationProfile profile, CancellationToken token);
}
