using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

public static class OnboardingProtocol
{
    public const string COMPATIBILITY = "2.0/2/2/2/1/6.10.0";
    public const string TRUST_COMPATIBILITY = "3.0/3/3/3/1/6.10.0";

    public static bool Supported(ClusterOptions options)
    {
        return options.Enabled && options.Compatibility is COMPATIBILITY or TRUST_COMPATIBILITY;
    }

    public static void ValidateSnapshot(RevisionImage image, ClusterOptions options)
    {
        bool onboarding = image.Onboarding is not null || image.OnboardingContracts is not null
            || HasHistory(image.RecoveryBaseline);
        if (onboarding && !Supported(options))
        {
            throw new InvalidDataException("OnboardingSnapshotIncompatible");
        }

        if (HasTrust(image.OnboardingContracts) || HasTrustHistory(image.RecoveryBaseline))
        {
            if (!options.Enabled || options.Compatibility != TRUST_COMPATIBILITY)
            {
                throw new InvalidDataException("EnrollmentTrustSnapshotIncompatible");
            }
        }
    }

    private static bool HasTrust(OnboardingContractImage? contracts)
    {
        return contracts?.Issuers is not null || contracts?.GenesisInitialized == true
            || contracts?.OnlineGrants is not null || contracts?.OnlineProcesses is not null;
    }

    private static bool HasTrustHistory(RecoveryBaseline? baseline)
    {
        return baseline is not null && (HasTrust(baseline.PreviousOnboardingContracts) || baseline.Ancestors.Any(HasTrustHistory));
    }

    private static bool HasHistory(RecoveryBaseline? baseline)
    {
        return baseline is not null && (baseline.PreviousOnboarding is not null || baseline.PreviousOnboardingContracts is not null
            || baseline.Ancestors.Any(HasHistory));
    }
}
