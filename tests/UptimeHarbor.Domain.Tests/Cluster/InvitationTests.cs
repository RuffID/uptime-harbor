using UptimeHarbor.Domain.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Domain.Cluster;

public class InvitationTests
{
    [Theory]
    [InlineData(InvitationStatus.Revoked)]
    [InlineData(InvitationStatus.Expired)]
    [InlineData(InvitationStatus.Consumed)]
    public void ClosedCodeCannotBecomeUnusedOrReserved(InvitationStatus status)
    {
        // Arrange
        InvitationStatus next = InvitationStatus.Reserved;

        // Act
        Exception? failure = Record.Exception(() => InvitationTransitions.Change(status, next));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
    }

    [Fact]
    public void ReservationMayOnlyBeConsumedOrRevoked()
    {
        // Arrange
        InvitationStatus reserved = InvitationTransitions.Change(InvitationStatus.Unused, InvitationStatus.Reserved);

        // Act
        InvitationStatus consumed = InvitationTransitions.Change(reserved, InvitationStatus.Consumed);
        Exception? expired = Record.Exception(() => InvitationTransitions.Change(reserved, InvitationStatus.Expired));

        // Assert
        Assert.Equal(InvitationStatus.Consumed, consumed);
        Assert.IsType<InvalidOperationException>(expired);
    }

    [Fact]
    public void AttemptCannotSkipCertificateBindingOrMembership()
    {
        // Arrange
        OnboardingAttemptStatus reserved = OnboardingAttemptStatus.Reserved;

        // Act
        OnboardingAttemptStatus next = InvitationTransitions.Advance(reserved);
        Exception? terminal = Record.Exception(() => InvitationTransitions.Advance(OnboardingAttemptStatus.Cancelled));

        // Assert
        Assert.Equal(OnboardingAttemptStatus.CertificateAuthorized, next);
        Assert.IsType<InvalidOperationException>(terminal);
    }
}
