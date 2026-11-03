namespace UptimeHarbor.Domain.Cluster;

public enum OnboardingAttemptStatus
{
    Reserved,
    CertificateAuthorized,
    CredentialBound,
    Admitted,
    MembershipAccepted,
    Completed,
    PauseAwaitingAdministrator,
    Cancelled
}
