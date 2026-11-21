using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Application.Cluster.Renewal;

public class VoterCredentialLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SerializedJournalReplayKeepsOriginalDecisionAcrossTermsAndRejectsIdReuse(bool retire)
    {
        // Arrange
        RenewalScenario scenario = new();
        RevisionImage initial = scenario.State;
        List<byte[]> journal = [];
        foreach (string action in new[] { "AuthorizeVoterCredential", "BindVoterCredential", "RetireOldCredential" })
        {
            journal.Add(RevisionCodec.Encode(scenario.Commit(action)));
        }

        RevisionImage replayed = initial;
        RevisionReducer reducer = new();

        // Act
        foreach (byte[] record in journal)
        {
            replayed = reducer.Apply(replayed, RevisionCodec.Decode(record), replayed.Index + 1, replayed.Term);
        }

        RevisionCommand original = RevisionCodec.Decode(retire ? journal[^1] : journal[0]);
        EnrollmentTrustCommand trust = original.EnrollmentTrust!;
        RevisionCommand changed = original with { CreatedAtUtc = original.CreatedAtUtc.AddTicks(1) };
        if (retire)
        {
            changed = original with { EnrollmentTrust = trust with
            {
                Rotation = trust.Rotation! with { InstallationProofDigest = new string('D', 64) }
            } };
        }
        Exception? collision = Record.Exception(() => reducer.Apply(replayed, changed, replayed.Index + 1, replayed.Term));
        RevisionCommand newLeaderReplay = original with { EnrollmentTrust = trust with
        {
            CoordinatorAtUtc = scenario.Now.AddDays(40), CoordinatorTerm = replayed.Term + 1
        } };
        RevisionImage retried = reducer.Apply(replayed, newLeaderReplay, replayed.Index + 1, replayed.Term + 1);

        // Assert
        Assert.Equal(StrictOnboardingJson.Encode(scenario.State), StrictOnboardingJson.Encode(replayed));
        Assert.IsType<InvalidDataException>(collision);
        Assert.Equal("IdempotencyKeyReuse", collision.Message);
        Assert.Equal(replayed.OnboardingContracts, retried.OnboardingContracts);
        Assert.Equal(replayed.Operations, retried.Operations);
        Assert.Equal(replayed.Results[original.OperationId], retried.Results[original.OperationId]);
    }

    [Fact]
    public void LifecycleIsIndependentOfAttemptAndChangesCredentialsOnlyAfterBind()
    {
        // Arrange
        RenewalScenario scenario = new();
        RevisionImage initial = scenario.State;

        // Act
        scenario.Commit("AuthorizeVoterCredential");

        // Assert
        Assert.Null(scenario.State.Onboarding);
        Assert.Single(scenario.State.Operations.Credentials);
        Assert.Equal(0, scenario.Rotation.CredentialRevision);
        Assert.Empty(RevisionWriteSetFactory.Create(initial, scenario.State, scenario.Node).Records.OfType<PeerTrustCredential>());

        // Act
        RevisionImage authorized = scenario.State;
        scenario.Commit("BindVoterCredential");
        VoterCredentialRotation bound = scenario.Rotation;

        // Assert
        Assert.Equal(scenario.State.Index, bound.CredentialRevision);
        Assert.Equal(scenario.Now.AddHours(24), bound.OverlapUntilUtc);
        Assert.Equal(initial.Operations.Nodes, scenario.State.Operations.Nodes);
        Assert.Equal(initial.Operations.MembershipVersion, scenario.State.Operations.MembershipVersion);
        Assert.Equal(initial.Operations.Changes, scenario.State.Operations.Changes);
        OperationalCredential credential = scenario.State.Operations.Credentials[scenario.Intent.Issuance.IssueId];
        Assert.Null(credential.AdmissionId);
        Assert.Single(RevisionWriteSetFactory.Create(authorized, scenario.State, scenario.Node).Records.OfType<PeerTrustCredential>());

        // Act
        scenario.Commit("RetireOldCredential");

        // Assert
        Assert.Equal(bound.CredentialRevision, scenario.Rotation.CredentialRevision);
        Assert.False(VoterCredentialPolicy.IsUsable(scenario.State, scenario.Old, scenario.Now));
        Assert.True(VoterCredentialPolicy.IsUsable(scenario.State, credential, scenario.Now));
        Assert.Null(scenario.Old.RevokedAtUtc);
        Assert.NotNull(scenario.Rotation.InstallationProofDigest);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void RenewWindowIncludesExactlySevenDays(long extraTicks, bool allowed)
    {
        // Arrange
        RenewalScenario scenario = new();
        OperationalCredential old = scenario.Old with { ExpiresAtUtc = scenario.Now.AddDays(7).AddTicks(extraTicks) };
        scenario.State = scenario.State with { Operations = scenario.State.Operations with
        {
            Credentials = scenario.State.Operations.Credentials.SetItem(old.Id, old)
        } };

        // Act
        CommandResult result = scenario.Apply(scenario.Command("AuthorizeVoterCredential"));

        // Assert
        Assert.Equal(allowed, result.Outcome == OperationOutcome.Committed);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void ExpiredOldLeafNeverGetsRenewException(long remainingTicks, bool allowed)
    {
        // Arrange
        RenewalScenario scenario = new();
        OperationalCredential old = scenario.Old with { ExpiresAtUtc = scenario.Now.AddTicks(remainingTicks) };
        scenario.State = scenario.State with { Operations = scenario.State.Operations with
        {
            Credentials = scenario.State.Operations.Credentials.SetItem(old.Id, old)
        } };

        // Act
        CommandResult result = scenario.Apply(scenario.Command("AuthorizeVoterCredential"));

        // Assert
        Assert.Equal(allowed, result.Outcome == OperationOutcome.Committed);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void BindRechecksOldExpiry(long offsetTicks, bool allowed)
    {
        // Arrange
        RenewalScenario scenario = new();
        scenario.Commit("AuthorizeVoterCredential");
        scenario.Now = scenario.Old.ExpiresAtUtc.AddTicks(offsetTicks);

        // Act
        CommandResult result = scenario.Apply(scenario.Command("BindVoterCredential"));

        // Assert
        Assert.Equal(allowed, result.Outcome == OperationOutcome.Committed);
        Assert.Equal(allowed, scenario.State.Operations.Credentials.ContainsKey(scenario.Intent.Issuance.IssueId));
    }

    [Theory]
    [InlineData(48, 24)]
    [InlineData(24, 24)]
    [InlineData(6, 6)]
    public void OverlapStopsAtEarlierOf24HoursOrOldExpiry(int hoursLeft, int overlapHours)
    {
        // Arrange
        RenewalScenario scenario = new();
        scenario.Commit("AuthorizeVoterCredential");
        scenario.Now = scenario.Old.ExpiresAtUtc.AddHours(-hoursLeft);
        scenario.Commit("BindVoterCredential");
        DateTimeOffset until = scenario.Rotation.OverlapUntilUtc!.Value;
        OperationalCredential next = scenario.State.Operations.Credentials[scenario.Intent.Issuance.IssueId];

        // Act
        bool before = VoterCredentialPolicy.IsUsable(scenario.State, scenario.Old, until.AddTicks(-1));
        bool at = VoterCredentialPolicy.IsUsable(scenario.State, scenario.Old, until);
        bool after = VoterCredentialPolicy.IsUsable(scenario.State, scenario.Old, until.AddTicks(1));

        // Assert
        Assert.Equal(scenario.Now.AddHours(overlapHours), until);
        Assert.True(before);
        Assert.False(at);
        Assert.False(after);
        Assert.True(VoterCredentialPolicy.IsUsable(scenario.State, next, until));
        Assert.Null(scenario.Rotation.RetiredIndex);
    }

    [Theory]
    [InlineData("AuthorizeVoterCredential")]
    [InlineData("BindVoterCredential")]
    [InlineData("RetireOldCredential")]
    public void ReplayReturnsOriginalDecisionWithoutExtendingLeaseOrReplacingBytes(string action)
    {
        // Arrange
        RenewalScenario scenario = new();
        if (action != "AuthorizeVoterCredential")
        {
            scenario.Commit("AuthorizeVoterCredential");
        }

        if (action == "RetireOldCredential")
        {
            scenario.Commit("BindVoterCredential");
        }

        RevisionCommand original = scenario.Commit(action);
        RevisionImage before = scenario.State;
        RevisionCommand retry = original with { EnrollmentTrust = original.EnrollmentTrust! with
        {
            CoordinatorAtUtc = scenario.Now.AddYears(2), CoordinatorTerm = scenario.State.Term
        } };

        // Act
        CommandResult replayed = scenario.Apply(retry);

        // Assert
        Assert.Equal(before.Results[original.OperationId], replayed);
        Assert.Equal(before.OnboardingContracts, scenario.State.OnboardingContracts);
        Assert.Equal(before.Operations, scenario.State.Operations);
        Assert.Equal(RevisionCodec.Digest(original), RevisionCodec.Digest(retry));
        Assert.NotEqual(RevisionCodec.Digest(RevisionCodec.Encode(original)), RevisionCodec.Digest(RevisionCodec.Encode(retry)));
    }

    [Theory]
    [InlineData("BindVoterCredential")]
    [InlineData("RetireOldCredential")]
    public void LaterStageCannotBeInventedWithoutItsAcceptedPredecessor(string action)
    {
        // Arrange
        RenewalScenario scenario = new();

        // Act
        CommandResult result = scenario.Apply(scenario.Command(action));

        // Assert
        Assert.Equal(OperationOutcome.Rejected, result.Outcome);
        Assert.Single(scenario.State.Operations.Credentials);
    }

    [Fact]
    public void SecondRotationRequiresRetirementCurrentKeyAndExactCredentialRevision()
    {
        // Arrange
        RenewalScenario scenario = new();
        scenario.Commit("AuthorizeVoterCredential");
        scenario.Commit("BindVoterCredential");
        VoterCredentialRotation first = scenario.Rotation;
        OperationalCredential current = scenario.State.Operations.Credentials[scenario.Intent.Issuance.IssueId];
        VoterCredentialRenewal firstIntent = scenario.Intent;
        scenario.Now = current.ExpiresAtUtc.AddDays(-7);
        scenario.Intent = new(current.Id, current.Fingerprint, firstIntent.Issuance.SpkiDigest, first.CredentialRevision,
            firstIntent.Issuance with { IssueId = Guid.NewGuid(), SpkiDigest = new string('F', 64),
                NotBeforeUtc = scenario.Now.AddMinutes(-2), NotAfterUtc = scenario.Now.AddDays(30) });
        RevisionCommand next = scenario.Command("AuthorizeVoterCredential");

        // Act
        CommandResult busy = scenario.Apply(next);
        scenario.Intent = firstIntent;
        scenario.Commit("RetireOldCredential");
        scenario.Intent = next.EnrollmentTrust!.Rotation!.Renewal;
        CommandResult stale = scenario.Apply(scenario.Command("AuthorizeVoterCredential") with
        {
            BaseRevision = 0, EnrollmentTrust = next.EnrollmentTrust with
            {
                Rotation = next.EnrollmentTrust.Rotation with { Renewal = scenario.Intent with { PreviousCredentialRevision = 0 } }
            }
        });
        CommandResult accepted = scenario.Apply(scenario.Command("AuthorizeVoterCredential"));

        // Assert
        Assert.Equal(OperationOutcome.Rejected, busy.Outcome);
        Assert.Equal("VoterCredentialRevisionChanged", stale.Reason);
        Assert.Equal(OperationOutcome.Committed, accepted.Outcome);
        Assert.Equal(2, scenario.State.OnboardingContracts!.VoterCredentialRotations!.Count);
    }
}
