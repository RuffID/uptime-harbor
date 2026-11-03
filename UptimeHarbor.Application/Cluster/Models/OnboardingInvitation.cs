using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Models;

public record OnboardingInvitation(Guid Id, Guid IssueOperationId, string Verifier,
    DateTimeOffset IssuedAtUtc, DateTimeOffset ExpiresAtUtc, OnboardingDelegation Delegation)
{
    public InvitationStatus Status { get; init; } = InvitationStatus.Unused;
    public OnboardingAttempt? Attempt { get; init; }
    public Guid? ReplacesInvitationId { get; init; }

    public override string ToString()
    {
        return $"OnboardingInvitation {Id} {Status}";
    }
}
