namespace UptimeHarbor.Domain.Cluster;

/// <summary>Необратимые переходы приглашения; потреблённое приглашение не удаляет участника.</summary>
public static class InvitationTransitions
{
    public static InvitationStatus Change(InvitationStatus current, InvitationStatus next)
    {
        bool allowed = current == InvitationStatus.Unused
            && next is InvitationStatus.Reserved or InvitationStatus.Revoked or InvitationStatus.Expired;
        allowed |= current == InvitationStatus.Reserved
            && next is InvitationStatus.Consumed or InvitationStatus.Revoked;
        if (!allowed)
        {
            throw new InvalidOperationException("InvitationTransitionUnavailable");
        }

        return next;
    }

    public static bool IsTerminal(OnboardingAttemptStatus state)
    {
        return state is OnboardingAttemptStatus.Completed or OnboardingAttemptStatus.Cancelled;
    }

    public static OnboardingAttemptStatus Advance(OnboardingAttemptStatus current)
    {
        switch (current)
        {
            case OnboardingAttemptStatus.Reserved:
                return OnboardingAttemptStatus.CertificateAuthorized;
            case OnboardingAttemptStatus.CertificateAuthorized:
                return OnboardingAttemptStatus.CredentialBound;
            case OnboardingAttemptStatus.CredentialBound:
                return OnboardingAttemptStatus.Admitted;
            case OnboardingAttemptStatus.Admitted:
                return OnboardingAttemptStatus.MembershipAccepted;
            case OnboardingAttemptStatus.MembershipAccepted:
                return OnboardingAttemptStatus.Completed;
            default:
                throw new InvalidOperationException("AttemptTransitionUnavailable");
        }
    }
}
