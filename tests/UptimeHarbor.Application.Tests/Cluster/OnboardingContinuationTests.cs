using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Domain.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Application.Cluster;

public class OnboardingContinuationTests
{
    [Fact]
    public void NewReserveOperationCannotRenewExpiredAuthorityWhileOriginalReplayKeepsItsDecision()
    {
        // Arrange
        OnboardingScenario scenario = new();
        scenario.Apply(scenario.Issue());
        RevisionCommand original = scenario.Reserve();
        CommandResult first = scenario.Apply(original);
        scenario.Now = scenario.Invitation.Attempt!.ReservationUntilUtc;

        // Act
        CommandResult replay = scenario.Apply(original);
        CommandResult retry = scenario.Apply(scenario.Reserve());
        OnboardingSnapshot.Validate(scenario.State);

        // Assert
        Assert.Equal(first, replay);
        Assert.Equal("OnboardingAdministratorRequired", retry.Reason);
        Assert.Equal(OnboardingAttemptStatus.PauseAwaitingAdministrator, scenario.Invitation.Attempt!.Status);
        Assert.Equal(scenario.Now, scenario.Invitation.Attempt.ReservationUntilUtc);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void FreshPasswordAuthorizationExpiresAfterFiveMinutes(int ticks, bool accepted)
    {
        // Arrange
        OnboardingScenario scenario = new();
        RevisionCommand issue = scenario.Issue();
        scenario.Now = scenario.Now.AddMinutes(5).AddTicks(ticks);

        // Act
        CommandResult result = scenario.Apply(issue);

        // Assert
        Assert.Equal(accepted, result.Outcome == OperationOutcome.Committed);
        if (!accepted)
        {
            Assert.Empty(scenario.State.Onboarding!.Invitations);
        }
    }

    [Fact]
    public void WrongVerifierDoesNotReserveAndCanNeverBeLoggedAsSuccess()
    {
        // Arrange
        OnboardingScenario scenario = new();
        Assert.Equal(OperationOutcome.Committed, scenario.Apply(scenario.Issue()).Outcome);
        RevisionCommand reserve = scenario.Reserve();
        reserve = reserve with { Onboarding = reserve.Onboarding! with { Verifier = new string('F', 64) } };

        // Act
        CommandResult result = scenario.Apply(reserve);

        // Assert
        Assert.Equal(OperationOutcome.Rejected, result.Outcome);
        Assert.Equal("InvitationRejected", result.Reason);
        Assert.Equal(InvitationStatus.Unused, scenario.Invitation.Status);
        Assert.Null(scenario.Invitation.Attempt);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void AdmissionRenewalRequiresLessThanThreeMinutesRemaining(int ticks, bool accepted)
    {
        // Arrange
        OnboardingScenario scenario = new();
        scenario.Admit();
        scenario.Now = scenario.Now.AddMinutes(7).AddTicks(ticks);
        Assert.Equal(OperationOutcome.Committed, scenario.Apply(scenario.Step("RenewOnboardingProcess")).Outcome);

        // Act
        CommandResult result = scenario.Apply(scenario.Step("RenewOnboardingAdmission"));

        // Assert
        Assert.Equal(accepted, result.Outcome == OperationOutcome.Committed);
    }

    [Fact]
    public void NewCodeAfterAdmitDoesNotCancelOldAttemptAndCannotReserveSecondJoin()
    {
        // Arrange
        OnboardingScenario scenario = new();
        scenario.Admit();
        Guid newId = Guid.NewGuid();
        RevisionCommand issue = scenario.Command("ReplaceInvitation", new OnboardingCommand
        {
            StorageGeneration = scenario.Storage, InvitationId = newId, ReplacesInvitationId = scenario.InvitationId,
            Verifier = new string('E', 64), ReauthenticatedAtUtc = scenario.Now
        });

        // Act
        CommandResult issued = scenario.Apply(issue);
        RevisionCommand reserve = scenario.Reserve(scenario.Request with
        {
            InvitationId = newId, AttemptId = Guid.NewGuid(), NodeId = Guid.NewGuid(), StorageGeneration = Guid.NewGuid(), SpkiDigest = new string('A', 64)
        });
        reserve = reserve with { EntityId = newId, Onboarding = reserve.Onboarding! with { InvitationId = newId, Verifier = new string('E', 64) } };
        CommandResult denied = scenario.Apply(reserve);

        // Assert
        Assert.Equal(OperationOutcome.Committed, issued.Outcome);
        Assert.Equal(OperationOutcome.Rejected, denied.Outcome);
        Assert.Equal(InvitationStatus.Consumed, scenario.Invitation.Status);
        Assert.Equal(OnboardingAttemptStatus.Admitted, scenario.Invitation.Attempt!.Status);
        Assert.True(scenario.State.Operations.Nodes.ContainsKey(scenario.Request.NodeId));
        Assert.False(scenario.State.Operations.Nodes[scenario.Request.NodeId].Removed);
    }

    [Fact]
    public void CancelNonvoterRevokesCredentialWhileRevokeAfterAdmitPreservesMembership()
    {
        // Arrange
        OnboardingScenario scenario = new();
        scenario.Admit();
        RevisionCommand revoke = scenario.Command("RevokeInvitation", new OnboardingCommand
        {
            StorageGeneration = scenario.Storage, InvitationId = scenario.InvitationId, ReauthenticatedAtUtc = scenario.Now
        });

        // Act
        CommandResult revoked = scenario.Apply(revoke);
        RevisionCommand cancel = scenario.Command("CancelOnboardingAttempt", new OnboardingCommand
        {
            StorageGeneration = scenario.Storage, InvitationId = scenario.InvitationId, AttemptId = scenario.Request.AttemptId,
            ReauthenticatedAtUtc = scenario.Now
        });
        CommandResult cancelled = scenario.Apply(cancel);

        // Assert
        Assert.Equal("MembershipPreservedUseCancelAttempt", revoked.Reason);
        Assert.Equal(OperationOutcome.Committed, cancelled.Outcome);
        Assert.Equal(OnboardingAttemptStatus.Cancelled, scenario.Invitation.Attempt!.Status);
        Assert.Equal(InvitationStatus.Consumed, scenario.Invitation.Status);
        Assert.NotNull(Assert.Single(scenario.State.Operations.Credentials.Values).RevokedAtUtc);
        Assert.True(scenario.State.Operations.Nodes.ContainsKey(scenario.Request.NodeId));
        Assert.True(scenario.State.Operations.Nodes[scenario.Request.NodeId].Removed);
    }

    [Fact]
    public void DelegatedMembershipUsesSameReducerAndLegacyCommandCannotBypassCoordinatorTimestamp()
    {
        // Arrange
        OnboardingScenario scenario = new();
        scenario.Admit();
        RevisionCommand old = scenario.Journal.Command(scenario.Request.NodeId, DeletedEntityKind.Node, scenario.State.Operations.Revision, null) with
        {
            Action = "RequestMembership", Operation = new ClusterOperation { StorageGeneration = scenario.Storage, MembershipVersion = 1 }
        };

        // Act
        CommandResult legacy = scenario.Journal.Apply(old);
        RevisionCommand request = scenario.Step("RequestOnboardingMembership");
        CommandResult accepted = scenario.Apply(request);
        RevisionCommand confirm = scenario.Step("ConfirmOnboardingMembership");
        confirm = confirm with { Onboarding = confirm.Onboarding! with { MembershipChangeId = request.OperationId, MembershipVersion = 2 } };
        CommandResult confirmed = scenario.Apply(confirm);

        // Assert
        Assert.Equal("OnboardingProtocolRequired", legacy.Reason);
        Assert.Equal(OperationOutcome.Committed, accepted.Outcome);
        Assert.Equal(OperationOutcome.Committed, confirmed.Outcome);
        Assert.True(scenario.State.Operations.Nodes[scenario.Request.NodeId].Voting);
        Assert.False(scenario.State.Operations.Nodes[scenario.Request.NodeId].Activated);
        Assert.Equal(OnboardingAttemptStatus.MembershipAccepted, scenario.Invitation.Attempt!.Status);
        Assert.NotNull(Assert.Single(scenario.State.Operations.Credentials.Values).ConsumedAtUtc);
    }

    [Fact]
    public void SnapshotOfConsumedInvitationKeepsExactOutcomeAndRejectsNewReservation()
    {
        // Arrange
        OnboardingScenario scenario = new();
        scenario.Admit();
        RevisionImage before = scenario.State;

        // Act
        RevisionImage restored = StrictOnboardingJson.Decode<RevisionImage>(StrictOnboardingJson.Encode(before));
        OnboardingSnapshot.Validate(restored);
        scenario.Journal.State = restored;
        CommandResult denied = scenario.Apply(scenario.Reserve());

        // Assert
        Assert.Equal(OperationOutcome.Rejected, denied.Outcome);
        Assert.Equal(InvitationStatus.Consumed, scenario.Invitation.Status);
        Assert.Equal(before.Results[scenario.InvitationId], restored.Results[scenario.InvitationId]);
        Assert.Equal(before.Onboarding!.Invitations[scenario.InvitationId].Attempt!.AdmitOperationId, scenario.Invitation.Attempt!.AdmitOperationId);
    }

    [Fact]
    public void VerifiedProgressAllowsCatchupBeyondTwentyFourHours()
    {
        // Arrange
        OnboardingScenario scenario = new();
        scenario.Admit();
        DateTimeOffset started = scenario.Now;

        // Act
        for (int hour = 1; hour <= 25; hour++)
        {
            scenario.Now = started.AddHours(hour);
            Assert.Equal(OperationOutcome.Committed, scenario.Apply(scenario.Step("RenewOnboardingProcess")).Outcome);
            Assert.Equal(OperationOutcome.Committed, scenario.Apply(scenario.Step("RenewOnboardingAdmission")).Outcome);
            RevisionCommand progress = scenario.Step("RecordOnboardingProgress");
            progress = progress with { Onboarding = progress.Onboarding! with { ProgressIndex = scenario.State.Index } };
            Assert.Equal(OperationOutcome.Committed, scenario.Apply(progress).Outcome);
        }

        // Assert
        Assert.Equal(OnboardingAttemptStatus.Admitted, scenario.Invitation.Attempt!.Status);
        Assert.Equal(started.AddHours(25), scenario.Invitation.Attempt.LastProgressAtUtc);
        Assert.Equal(InvitationStatus.Consumed, scenario.Invitation.Status);
    }

    [Fact]
    public void CancellingPausedAttemptProducesRestorableTombstone()
    {
        // Arrange
        OnboardingScenario scenario = new();
        scenario.Admit();
        scenario.Now = scenario.Now.AddHours(24);
        scenario.Apply(scenario.Step("RenewOnboardingProcess"));
        Assert.Equal(OnboardingAttemptStatus.PauseAwaitingAdministrator, scenario.Invitation.Attempt!.Status);
        RevisionCommand cancel = scenario.Command("CancelOnboardingAttempt", new OnboardingCommand
        {
            StorageGeneration = scenario.Storage, InvitationId = scenario.InvitationId, AttemptId = scenario.Request.AttemptId,
            ReauthenticatedAtUtc = scenario.Now
        });

        // Act
        CommandResult result = scenario.Apply(cancel);
        OnboardingSnapshot.Validate(scenario.State);

        // Assert
        Assert.Equal(OperationOutcome.Committed, result.Outcome);
        Assert.Null(scenario.Invitation.Attempt!.ResumeStatus);
        Assert.True(scenario.State.Operations.Nodes[scenario.Request.NodeId].Removed);
    }

    [Fact]
    public void MembershipRequiresNewDelegationAfterAuthorChangesAndCancelNeverRemovesVoter()
    {
        // Arrange
        OnboardingScenario scenario = new();
        scenario.Admit();
        RevisionCommand request = scenario.Step("RequestOnboardingMembership");
        Assert.Equal(OperationOutcome.Committed, scenario.Apply(request).Outcome);
        AcceptedValue actor = scenario.State.Values[scenario.Journal.Actor];
        scenario.Journal.State = scenario.State with { Values = scenario.State.Values.SetItem(actor.Id, actor with { Revision = 3 }) };
        RevisionCommand confirm = scenario.Step("ConfirmOnboardingMembership");
        confirm = confirm with { Onboarding = confirm.Onboarding! with { MembershipChangeId = request.OperationId, MembershipVersion = 2 } };

        // Act
        CommandResult denied = scenario.Apply(confirm);
        Assert.Equal(OperationOutcome.Committed, scenario.Apply(scenario.Command("ContinueOnboardingAttempt", new OnboardingCommand
        {
            StorageGeneration = scenario.Storage, InvitationId = scenario.InvitationId, AttemptId = scenario.Request.AttemptId,
            ReauthenticatedAtUtc = scenario.Now
        })).Outcome);
        RevisionCommand authorize = scenario.Step("AuthorizeOnboardingMembership");
        authorize = authorize with { Onboarding = authorize.Onboarding! with { MembershipChangeId = request.OperationId } };
        CommandResult authorized = scenario.Apply(authorize);
        confirm = confirm with { OperationId = Guid.NewGuid(), BaseRevision = scenario.State.Onboarding!.Revision };
        CommandResult confirmed = scenario.Apply(confirm);
        CommandResult cancelled = scenario.Apply(scenario.Command("CancelOnboardingAttempt", new OnboardingCommand
        {
            StorageGeneration = scenario.Storage, InvitationId = scenario.InvitationId, AttemptId = scenario.Request.AttemptId,
            ReauthenticatedAtUtc = scenario.Now
        }));

        // Assert
        Assert.Equal("OnboardingAdministratorRequired", denied.Reason);
        Assert.Equal(OperationOutcome.Committed, authorized.Outcome);
        Assert.Equal(OperationOutcome.Committed, confirmed.Outcome);
        Assert.Equal("MembershipPreservedRemovalRequired", cancelled.Reason);
        Assert.True(scenario.State.Operations.Nodes[scenario.Request.NodeId].Voting);
        Assert.False(scenario.State.Operations.Nodes[scenario.Request.NodeId].Removed);
    }
}
