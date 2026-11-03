using UptimeHarbor.Application.Installation;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Только immutable durable профиль; отсутствие профиля в Setup закрывает gates.</summary>
public class InstallationState
{
    private LocalInstallationProfile? current;
    private bool commonOpen;
    public LocalInstallationProfile? Current => Volatile.Read(ref current);
    public SemaphoreSlim Transition { get; } = new(1, 1);

    public void Publish(LocalInstallationProfile profile)
    {
        LocalInstallationProfile? previous = Current;
        if (previous is not null && (previous.InstallIdentity != profile.InstallIdentity
            || profile.ProfileRevision < previous.ProfileRevision || profile.RuntimeGeneration < previous.RuntimeGeneration
            || profile.ProfileRevision == previous.ProfileRevision && profile != previous))
        {
            throw new InvalidOperationException("StaleInstallationPublication");
        }

        Volatile.Write(ref current, profile);
        if (profile.Phase != InstallationPhase.ClusterReady)
        {
            Volatile.Write(ref commonOpen, false);
        }
    }

    public void ConfirmCommonOpen(LocalInstallationProfile profile)
    {
        if (profile != Current || profile.Phase != InstallationPhase.ClusterReady)
        {
            throw new InvalidOperationException("StaleInstallationPublication");
        }

        Volatile.Write(ref commonOpen, true);
    }

    public bool CommonOpen => Volatile.Read(ref commonOpen);
}
