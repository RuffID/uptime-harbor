using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

public static class OnboardingProtocol
{
    public const string COMPATIBILITY = "2.0/2/2/2/1/6.10.0";

    public static bool Supported(ClusterOptions options)
    {
        return options.Enabled && options.Compatibility == COMPATIBILITY;
    }

    public static void ValidateSnapshot(RevisionImage image, ClusterOptions options)
    {
        bool onboarding = image.Onboarding is not null || image.OnboardingContracts is not null
            || HasHistory(image.RecoveryBaseline);
        if (onboarding && !Supported(options))
        {
            throw new InvalidDataException("OnboardingSnapshotIncompatible");
        }
    }

    private static bool HasHistory(RecoveryBaseline? baseline)
    {
        return baseline is not null && (baseline.PreviousOnboarding is not null || baseline.PreviousOnboardingContracts is not null
            || baseline.Ancestors.Any(HasHistory));
    }
}
