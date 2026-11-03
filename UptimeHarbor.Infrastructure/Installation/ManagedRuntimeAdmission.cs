using Microsoft.Extensions.DependencyInjection;
using UptimeHarbor.Application.Installation;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Отдельные проверки quarantined start и рабочего допуска. Не создаёт native runtime.</summary>
public class ManagedRuntimeAdmission(IServiceScopeFactory scopes, ManagedAdmissionState admission,
    InstallationState installation, OnlineEnableState enable) : IManagedRuntimeAdmission
{
    public async Task<bool> CanStartAsync(LocalInstallationProfile profile, CancellationToken token)
    {
        if (profile != installation.Current || profile.Phase != InstallationPhase.ClusterQuarantine)
        {
            return false;
        }

        if (admission.Read() is not null)
        {
            return admission.AllowsConsensus(Guid.Empty);
        }

        if (profile.Mode != "Bootstrap")
        {
            return false;
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        if (profile.ManagedRestart)
        {
            return await scope.ServiceProvider.GetRequiredService<ManagedRestartPreparation>().PrepareAsync(profile, token)
                && admission.AllowsConsensus(Guid.Empty);
        }

        return await scope.ServiceProvider.GetRequiredService<GenesisRuntimePreparation>().PrepareAsync(profile, token)
            && admission.AllowsConsensus(Guid.Empty);
    }

    public Task<bool> CanWorkAsync(LocalInstallationProfile profile, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(profile == installation.Current && profile.Phase == InstallationPhase.ClusterReady
            && admission.AllowsConsensus(Guid.Empty) && enable.Allows());
    }
}
