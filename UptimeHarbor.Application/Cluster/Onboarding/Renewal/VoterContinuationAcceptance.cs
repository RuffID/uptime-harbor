namespace UptimeHarbor.Application.Cluster.Onboarding.Renewal;

public record VoterContinuationAcceptance(DateTimeOffset AtUtc, long MinimumIndex, long Term,
    long MembershipVersion, long RecoveryVersion);
