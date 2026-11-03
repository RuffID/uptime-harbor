using System.Text;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Domain.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Application.Cluster;

public class OnboardingTests
{
    [Fact]
    public void IssueStoresOnlyVerifierAndStartsTtlAtAcceptedCoordinatorTime()
    {
        // Arrange
        OnboardingScenario scenario = new();
        RevisionCommand command = scenario.Issue();
        scenario.Now = scenario.Now.AddMinutes(2);

        // Act
        CommandResult result = scenario.Apply(command);
        string json = Encoding.UTF8.GetString(StrictOnboardingJson.Encode(scenario.State));

        // Assert
        Assert.Equal(OperationOutcome.Committed, result.Outcome);
        Assert.Equal(scenario.Now.AddMinutes(15), scenario.Invitation.ExpiresAtUtc);
        Assert.DoesNotContain(scenario.Code.ToString("D"), json);
        Assert.DoesNotContain(scenario.Invitation.Verifier, scenario.Invitation.ToString());
        Assert.Equal(command.ActorUserId, scenario.Invitation.Delegation.ActorId);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void ReservationHasExactFifteenMinuteBoundary(int ticks, bool accepted)
    {
        // Arrange
        OnboardingScenario scenario = new();
        Assert.Equal(OperationOutcome.Committed, scenario.Apply(scenario.Issue()).Outcome);
        scenario.Now = scenario.Invitation.ExpiresAtUtc.AddTicks(ticks);

        // Act
        CommandResult result = scenario.Apply(scenario.Reserve());

        // Assert
        Assert.Equal(accepted, result.Outcome == OperationOutcome.Committed);
        if (!accepted)
        {
            Assert.Equal(InvitationStatus.Expired, scenario.Invitation.Status);
            Assert.Null(scenario.Invitation.Attempt);
        }
    }

    [Fact]
    public void CompetingIdentitiesAndChangedDigestCannotReuseReservation()
    {
        // Arrange
        OnboardingScenario scenario = new();
        Assert.Equal(OperationOutcome.Committed, scenario.Apply(scenario.Issue()).Outcome);
        RevisionCommand first = scenario.Reserve();
        RevisionCommand second = scenario.Reserve(scenario.Request with { NodeId = Guid.NewGuid(), AttemptId = Guid.NewGuid() });

        // Act
        CommandResult winner = scenario.Apply(first);
        CommandResult loser = scenario.Apply(second);
        CommandResult exact = scenario.Apply(first);
        CommandResult changed = scenario.Apply(scenario.Reserve(scenario.Request with { SpkiDigest = new string('E', 64) }));

        // Assert
        Assert.Equal(OperationOutcome.Committed, winner.Outcome);
        Assert.Equal(OperationOutcome.Rejected, loser.Outcome);
        Assert.Equal(winner, exact);
        Assert.Equal(OperationOutcome.Rejected, changed.Outcome);
        Assert.Equal(scenario.Request, scenario.Invitation.Attempt!.Request);
        Assert.Equal(scenario.Now.AddMinutes(30), scenario.Invitation.Attempt.ReservationUntilUtc);
    }

    [Fact]
    public void ExplicitReplacementCancelsReservedAttemptAndOldSnapshotCannotReviveCode()
    {
        // Arrange
        OnboardingScenario scenario = new();
        scenario.PrepareReservation();
        Guid newId = Guid.NewGuid();
        RevisionCommand replace = scenario.Command("ReplaceInvitation", new OnboardingCommand
        {
            StorageGeneration = scenario.Storage, InvitationId = newId, ReplacesInvitationId = scenario.InvitationId,
            Verifier = new string('E', 64), ReauthenticatedAtUtc = scenario.Now
        });

        // Act
        CommandResult result = scenario.Apply(replace);
        RevisionImage roundTrip = StrictOnboardingJson.Decode<RevisionImage>(StrictOnboardingJson.Encode(scenario.State));
        OnboardingSnapshot.Validate(roundTrip);
        scenario.Journal.State = roundTrip;
        CommandResult retry = scenario.Apply(scenario.Reserve());

        // Assert
        Assert.Equal(OperationOutcome.Committed, result.Outcome);
        Assert.Equal(InvitationStatus.Revoked, scenario.Invitation.Status);
        Assert.Equal(OnboardingAttemptStatus.Cancelled, scenario.Invitation.Attempt!.Status);
        Assert.Equal(OperationOutcome.Rejected, retry.Outcome);
        Assert.Equal(InvitationStatus.Unused, roundTrip.Onboarding!.Invitations[newId].Status);
    }

    [Fact]
    public void ReissueRaceAndUnusedLimitNeverLeaveTwoActiveCodes()
    {
        // Arrange
        OnboardingScenario scenario = new();
        Assert.Equal(OperationOutcome.Committed, scenario.Apply(scenario.Issue()).Outcome);
        Guid replacement = Guid.NewGuid();
        RevisionCommand replace = scenario.Command("ReplaceInvitation", new OnboardingCommand
        {
            StorageGeneration = scenario.Storage, InvitationId = replacement, ReplacesInvitationId = scenario.InvitationId,
            Verifier = new string('E', 64), ReauthenticatedAtUtc = scenario.Now
        });
        RevisionCommand reserve = scenario.Reserve();

        // Act
        CommandResult replaced = scenario.Apply(replace);
        CommandResult reserved = scenario.Apply(reserve);

        // Assert
        Assert.Equal(OperationOutcome.Committed, replaced.Outcome);
        Assert.Equal(OperationOutcome.Rejected, reserved.Outcome);
        Assert.Single(scenario.State.Onboarding!.Invitations.Values, item => item.Status == InvitationStatus.Unused);
        Assert.Null(scenario.Invitation.Attempt);
    }

    [Fact]
    public void ActorRevisionChangePausesAttemptAndFreshContinueKeepsSameIdentity()
    {
        // Arrange
        OnboardingScenario scenario = new();
        scenario.PrepareReservation();
        AcceptedValue actor = scenario.State.Values[scenario.Journal.Actor];
        scenario.Journal.State = scenario.State with { Values = scenario.State.Values.SetItem(actor.Id, actor with { Revision = 3 }) };

        // Act
        CommandResult denied = scenario.Apply(scenario.Step("RenewOnboardingProcess"));
        RevisionCommand resume = scenario.Command("ContinueOnboardingAttempt", new OnboardingCommand
        {
            StorageGeneration = scenario.Storage, InvitationId = scenario.InvitationId, AttemptId = scenario.Request.AttemptId,
            ReauthenticatedAtUtc = scenario.Now
        });
        CommandResult continued = scenario.Apply(resume);

        // Assert
        Assert.Equal("OnboardingAdministratorRequired", denied.Reason);
        Assert.Equal(OperationOutcome.Committed, continued.Outcome);
        Assert.Equal(scenario.Request, scenario.Invitation.Attempt!.Request);
        Assert.Equal(3, scenario.Invitation.Attempt.Delegation.ActorRevision);
        Assert.Equal(OnboardingAttemptStatus.Reserved, scenario.Invitation.Attempt.Status);
    }

    [Fact]
    public void DisabledAuthorCannotReserveAndRequestCannotSupplyAnotherActor()
    {
        // Arrange
        OnboardingScenario scenario = new();
        Assert.Equal(OperationOutcome.Committed, scenario.Apply(scenario.Issue()).Outcome);
        AcceptedValue actor = scenario.State.Values[scenario.Journal.Actor];
        scenario.Journal.State = scenario.State with { Values = scenario.State.Values.SetItem(actor.Id,
            actor with { Value = actor.Value with { Enabled = false } }) };
        RevisionCommand reserve = scenario.Reserve();

        // Act
        CommandResult denied = scenario.Apply(reserve);
        Exception? fakeActor = Record.Exception(() => RevisionCodec.Encode(reserve with { ActorUserId = scenario.Journal.OtherActor, ActorRevision = 2 }));

        // Assert
        Assert.Equal("DelegationChanged", denied.Reason);
        Assert.IsType<InvalidDataException>(fakeActor);
        Assert.Null(scenario.Invitation.Attempt);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    public void ReservationExpiryPausesWithoutPretendingPendingSucceeded(int ticks, bool paused)
    {
        // Arrange
        OnboardingScenario scenario = new();
        scenario.PrepareReservation();
        scenario.Now = scenario.Invitation.Attempt!.ReservationUntilUtc.AddTicks(ticks);

        // Act
        CommandResult result = scenario.Apply(scenario.Step("RenewOnboardingProcess"));

        // Assert
        Assert.Equal(paused, scenario.Invitation.Attempt!.Status == OnboardingAttemptStatus.PauseAwaitingAdministrator);
        Assert.Equal(!paused, result.Outcome == OperationOutcome.Committed);
        Assert.Equal(InvitationStatus.Reserved, scenario.Invitation.Status);
    }

    [Fact]
    public void ProcessTakeoverWaitsForLeaseAndInvalidatesOldEpoch()
    {
        // Arrange
        OnboardingScenario scenario = new();
        scenario.PrepareReservation();
        RevisionCommand old = scenario.Step("RenewOnboardingProcess");
        RevisionCommand resume = scenario.Step("ResumeOnboardingAttempt");
        resume = resume with { Onboarding = resume.Onboarding! with { BootId = Guid.NewGuid(), ProcessEpoch = 2 } };

        // Act
        CommandResult early = scenario.Apply(resume);
        scenario.Now = scenario.Invitation.Attempt!.ProcessLeaseUntilUtc;
        resume = resume with { OperationId = Guid.NewGuid(), BaseRevision = scenario.State.Onboarding!.Revision };
        CommandResult takeover = scenario.Apply(resume);
        old = old with { BaseRevision = scenario.State.Onboarding!.Revision };
        CommandResult stale = scenario.Apply(old);

        // Assert
        Assert.Equal("OnboardingProcessLeaseActive", early.Reason);
        Assert.Equal(OperationOutcome.Committed, takeover.Outcome);
        Assert.Equal("OnboardingProcessChanged", stale.Reason);
        Assert.Equal(2, scenario.Invitation.Attempt!.ProcessEpoch);
        Assert.Equal(scenario.Request, scenario.Invitation.Attempt.Request);
    }

    [Fact]
    public void AdmitConsumesInvitationAtomicallyWithoutVoteOrActivationAndDoesNotExpireAtFifteenMinutes()
    {
        // Arrange
        OnboardingScenario scenario = new();
        scenario.Admit();
        DateTimeOffset started = scenario.Now;
        scenario.Now = scenario.Now.AddMinutes(20);

        // Act
        CommandResult process = scenario.Apply(scenario.Step("RenewOnboardingProcess"));
        CommandResult renewal = scenario.Apply(scenario.Step("RenewOnboardingAdmission"));

        // Assert
        Assert.Equal(OperationOutcome.Committed, process.Outcome);
        Assert.Equal(OperationOutcome.Committed, renewal.Outcome);
        Assert.Equal(InvitationStatus.Consumed, scenario.Invitation.Status);
        Assert.False(scenario.State.Operations.Nodes[scenario.Request.NodeId].Voting);
        Assert.False(scenario.State.Operations.Nodes[scenario.Request.NodeId].Activated);
        Assert.Equal(started.AddMinutes(30), scenario.Invitation.Attempt!.AdmissionUntilUtc);
        Assert.Single(scenario.State.Operations.Credentials.Values, item => item.AdmissionId == scenario.Invitation.Attempt.AdmitOperationId);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    public void InactivityIsMeasuredFromVerifiedProgressAndPollCannotExtendIt(int ticks, bool paused)
    {
        // Arrange
        OnboardingScenario scenario = new();
        scenario.Admit();
        DateTimeOffset progress = scenario.Invitation.Attempt!.LastProgressAtUtc;
        scenario.Now = progress.AddHours(24).AddTicks(ticks);

        // Act
        CommandResult result = scenario.Apply(scenario.Step("RenewOnboardingProcess"));

        // Assert
        Assert.Equal(paused, scenario.Invitation.Attempt!.Status == OnboardingAttemptStatus.PauseAwaitingAdministrator);
        Assert.Equal(progress, scenario.Invitation.Attempt.LastProgressAtUtc);
        Assert.Equal(!paused, result.Outcome == OperationOutcome.Committed);
        Assert.True(scenario.State.Operations.Nodes.ContainsKey(scenario.Request.NodeId));
    }

    [Fact]
    public void CoordinatorEnvelopeDoesNotChangeRequestDigestButIsCoveredByJournalBytes()
    {
        // Arrange
        OnboardingScenario scenario = new();
        RevisionCommand original = scenario.Issue();
        RevisionCommand accepted = original with { Onboarding = original.Onboarding! with { CoordinatorAtUtc = scenario.Now, CoordinatorTerm = 1 } };

        // Act
        string request = RevisionCodec.Digest(original);
        string decision = RevisionCodec.Digest(accepted);
        string wal = RevisionCodec.Digest(RevisionCodec.Encode(accepted));

        // Assert
        Assert.Equal(request, decision);
        Assert.NotEqual(request, wal);
        Assert.Throws<InvalidDataException>(() => new RevisionReducer().Apply(scenario.State, original, 3, 1));
    }

    [Fact]
    public void CanonicalRequestRejectsWrongDigestAndSecretsNeverEnterParserErrors()
    {
        // Arrange
        OnboardingScenario scenario = new();
        RevisionCommand reserve = scenario.Reserve();
        reserve = reserve with { Onboarding = reserve.Onboarding! with { RequestDigest = new string('F', 64) } };
        string secret = scenario.Code.ToString("D");
        byte[] malformed = Encoding.UTF8.GetBytes("{\"secret\":\"" + secret + "\",\"secret\":0}");

        // Act
        Exception? digest = Record.Exception(() => RevisionCodec.Encode(reserve));
        Exception? json = Record.Exception(() => StrictOnboardingJson.Decode<EnrollmentRequest>(malformed));

        // Assert
        Assert.IsType<InvalidDataException>(digest);
        Assert.IsType<InvalidDataException>(json);
        Assert.DoesNotContain(secret, json.ToString());
        Assert.Null(json!.InnerException);
        Assert.DoesNotContain(secret, scenario.Request.ToString());
    }
}
