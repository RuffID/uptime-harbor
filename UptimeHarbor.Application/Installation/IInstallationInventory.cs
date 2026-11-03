namespace UptimeHarbor.Application.Installation;

public interface IInstallationInventory
{
    Task<InstallationInventory> ReadAsync(CancellationToken token);
}
