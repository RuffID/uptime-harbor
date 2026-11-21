using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;
using UptimeHarbor.Tests.Infrastructure.Cluster.Renewal;
using Xunit;
using Scenario = UptimeHarbor.Tests.Infrastructure.Installation.Renewal.LocalVoterRenewalTests.Scenario;

namespace UptimeHarbor.Tests.Infrastructure.Installation.Renewal;

public class LocalVoterRenewalRetryTests
{
    [Fact]
    public async Task ResendsSameRequestInsideWindowAfterLostDispatch()
    {
        // Arrange
        using Scenario scenario = new();
        RevisionCommand original = await DropBeforeAuthorize(scenario);
        scenario.Local.Authority.Time.Now = original.CreatedAtUtc.AddSeconds(10);
        scenario.Reopen(restoreBaseline: true);

        // Act
        DynamicPeerTls.Snapshot published = await scenario.RenewAsync(scenario.Baseline);

        // Assert
        Assert.Equal(original, scenario.Transport.Sent[1]);
        Assert.Equal(IssueId(original), published.CredentialId);
        Assert.Equal(["AuthorizeVoterCredential", "AuthorizeVoterCredential", "BindVoterCredential"],
            scenario.Transport.Sent.Select(item => item.Action));
        Assert.Equal(1, scenario.Local.Authority.Journal.Commits);
    }

    [Fact]
    public async Task LateCommitWinsAfterReadWithNoDecision()
    {
        // Arrange
        using Scenario scenario = new();
        RevisionCommand original = await DropBeforeAuthorize(scenario);
        AdvancePastWindow(scenario, original);
        scenario.Reopen(restoreBaseline: true);
        scenario.Transport.BeforeCommit = _ =>
        {
            scenario.Transport.BeforeCommit = null;
            Assert.Equal(OperationOutcome.Committed,
                scenario.Transport.Commit(original, original.CreatedAtUtc.AddSeconds(10)).Outcome);
        };

        // Act
        DynamicPeerTls.Snapshot published = await scenario.RenewAsync(scenario.Baseline);

        // Assert
        Assert.Equal(original, scenario.Transport.Sent[1]);
        Assert.Equal(IssueId(original), published.CredentialId);
        Assert.DoesNotContain(scenario.Local.Files.Writes, stage => stage.StartsWith("retry-", StringComparison.Ordinal));
        Assert.Equal(1, scenario.Local.Authority.Journal.Commits);
        Assert.Single(scenario.Transport.Sent, command => command.Action == "BindVoterCredential");
    }

    [Theory]
    [InlineData("lost-rejection")]
    [InlineData("before-write")]
    [InlineData("after-write")]
    public async Task ResumesAfterTerminalRejectionWithSameProtectedKey(string boundary)
    {
        // Arrange
        using Scenario scenario = new();
        RevisionCommand original = await DropBeforeAuthorize(scenario);
        LocalVoterCredentialIntent first = (await scenario.Local.Store.ReadAsync(scenario.Local.Reference, scenario.Local.Token)).Intent;
        string stage = $"retry-{original.OperationId:N}";
        byte[] originalEnvelope = File.ReadAllBytes(scenario.Local.PathFor("intent"));
        AdvancePastWindow(scenario, original);
        scenario.Local.Files.FailStage = stage;
        scenario.Local.Files.AfterWrite = boundary == "after-write";
        if (boundary == "lost-rejection")
        {
            scenario.Transport.FailAfter = "authorize-response";
            scenario.Local.Files.FailStage = null;
        }

        // Act
        await Assert.ThrowsAsync<IOException>(() => scenario.RenewAsync(scenario.Baseline));

        // Assert
        CommandResult rejection = scenario.Local.Authority.Scenario.State.Results[original.OperationId];
        Assert.Equal(OperationOutcome.Rejected, rejection.Outcome);
        Assert.Equal(original, scenario.Transport.Sent[1]);
        Assert.Equal(boundary == "after-write", File.Exists(scenario.Local.PathFor(stage)));
        Assert.Equal(0, scenario.Local.Authority.Journal.Commits);

        // Arrange
        scenario.Transport.FailAfter = null;
        Guid previousBoot = scenario.Local.Lease.BootId;
        scenario.Reopen(restoreBaseline: true);
        Assert.NotEqual(previousBoot, scenario.Local.Lease.BootId);

        // Act
        await scenario.Local.State.VoterRenewal.WaitAsync(scenario.Local.Token);
        Task<DynamicPeerTls.Snapshot> one = scenario.RenewAsync(scenario.Baseline);
        Task<DynamicPeerTls.Snapshot> two = scenario.RenewAsync(scenario.Baseline);
        scenario.Local.State.VoterRenewal.Release();
        DynamicPeerTls.Snapshot[] published = await Task.WhenAll(one, two);

        // Assert
        LocalVoterCredentialIntent replacement = (await scenario.Local.Store.ReadAsync(scenario.Local.Reference, scenario.Local.Token)).Intent;
        Assert.Same(published[0], published[1]);
        Assert.Equal(4, scenario.Transport.Sent.Count);
        Assert.Equal(rejection, scenario.Transport.Commit(original, original.CreatedAtUtc.AddSeconds(9)));
        Assert.Equal(originalEnvelope, File.ReadAllBytes(scenario.Local.PathFor("intent")));
        Assert.Equal(first.CsrDer, replacement.CsrDer);
        Assert.Equal(first with { Request = replacement.Request }, replacement);
        Assert.Equal(first.Request.Renewal.Issuance.SpkiDigest, replacement.Request.Renewal.Issuance.SpkiDigest);
        Assert.Equal(first.Request.Renewal.Issuance.IssuerEpoch, replacement.Request.Renewal.Issuance.IssuerEpoch);
        Assert.NotEqual(first.Request.Renewal.Issuance.Serial, replacement.Request.Renewal.Issuance.Serial);
        Assert.NotEqual(original.OperationId, replacement.Request.OperationId);
        Assert.NotEqual(IssueId(original), published[0].CredentialId);
        Assert.Equal(replacement.Request.Renewal.Issuance.IssueId, published[0].CredentialId);
        Assert.Equal(1, scenario.Local.Authority.Journal.Commits);
        Assert.Single(scenario.Transport.Sent, command => command.Action == "BindVoterCredential");
    }

    [Fact]
    public async Task LocalExpiryAndMissingResultCannotReplaceAnUndecidedRequest()
    {
        // Arrange
        using Scenario scenario = new();
        RevisionCommand original = await DropBeforeAuthorize(scenario);
        AdvancePastWindow(scenario, original);
        scenario.Transport.BeforeCommit = _ => throw new IOException("DecisionUnavailable");

        // Act
        await Assert.ThrowsAsync<IOException>(() => scenario.RenewAsync(scenario.Baseline));

        // Assert
        Assert.Equal(original, scenario.Transport.Sent[1]);
        Assert.False(scenario.Local.Authority.Scenario.State.Results.ContainsKey(original.OperationId));
        Assert.Equal(["intent"], scenario.Local.Files.Writes);
        Assert.Null(scenario.Local.Authority.Journal.Issued.GetValueOrDefault(IssueId(original)));
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("majority")]
    [InlineData("wrong-digest")]
    public async Task NoReplacementWithTerminalRejectionWhenCurrentAuthorityIsMissing(string failure)
    {
        // Arrange
        using Scenario scenario = new();
        RevisionCommand original = await DropBeforeAuthorize(scenario);
        AdvancePastWindow(scenario, original);
        Assert.Equal(OperationOutcome.Rejected, scenario.Transport.Commit(original).Outcome);
        VoterCredentialAuthorityFixture authority = scenario.Local.Authority;
        if (failure == "expiry")
        {
            authority.Time.Now = authority.Scenario.State.Operations.Credentials[scenario.Baseline].ExpiresAtUtc;
            scenario.PublishProcess(epoch: 4);
        }
        else if (failure == "majority")
        {
            authority.SubjectBarrier.Available = false;
        }
        else
        {
            RevisionImage state = authority.Scenario.State;
            CommandResult result = state.Results[original.OperationId];
            authority.Scenario.State = state with { Results = state.Results.SetItem(result.OperationId, result with { Digest = new string('0', 64) }) };
            authority.Publish();
        }

        // Act
        Exception? refusal = await Record.ExceptionAsync(() => scenario.RenewAsync(scenario.Baseline));

        // Assert
        Assert.NotNull(refusal);
        Assert.Single(scenario.Transport.Sent);
        Assert.Equal(["intent"], scenario.Local.Files.Writes);
        Assert.Equal(0, authority.Remote.Calls);
    }

    [Fact]
    public async Task ExactBindAfterReplacementRecoversWithOldLeafExpired()
    {
        // Arrange
        using Scenario scenario = new();
        RevisionCommand original = await DropBeforeAuthorize(scenario);
        AdvancePastWindow(scenario, original);
        scenario.Transport.FailAfter = "bind-response";
        await Assert.ThrowsAsync<IOException>(() => scenario.RenewAsync(scenario.Baseline));
        RevisionCommand bind = scenario.Transport.Sent.Last();
        VoterCredentialAuthorityFixture authority = scenario.Local.Authority;
        authority.Time.Now = authority.Scenario.State.Operations.Credentials[scenario.Baseline].ExpiresAtUtc;
        scenario.Reopen(restoreBaseline: false);

        // Act
        DynamicPeerTls.Snapshot published = await scenario.RenewAsync(scenario.Baseline);

        // Assert
        Assert.Equal(IssueId(bind), published.CredentialId);
        Assert.Equal(bind.EnrollmentTrust!.Rotation!.Credential!.Fingerprint, published.Fingerprint);
        Assert.Equal(4, scenario.Transport.Sent.Count);
        Assert.Equal(1, authority.Remote.Calls);
        Assert.Equal(1, authority.Journal.Commits);
    }

    [Fact]
    public async Task IssuerOutageKeepsAcceptedReplacementAndItsKeyCanProveTheNextRotation()
    {
        // Arrange
        using Scenario scenario = new();
        RevisionCommand original = await DropBeforeAuthorize(scenario);
        AdvancePastWindow(scenario, original);
        VoterCredentialAuthorityFixture authority = scenario.Local.Authority;
        authority.Remote.Available = false;

        // Act
        await Assert.ThrowsAsync<IOException>(() => scenario.RenewAsync(scenario.Baseline));

        // Assert
        RevisionCommand accepted = scenario.Transport.Sent.Last();
        Assert.NotEqual(original.OperationId, accepted.OperationId);
        Assert.Equal(OperationOutcome.Committed, authority.Scenario.State.Results[accepted.OperationId].Outcome);
        Assert.Equal(0, authority.Journal.Commits);

        // Arrange
        AdvancePastWindow(scenario, accepted);
        authority.Remote.Available = true;
        scenario.Reopen(restoreBaseline: true);

        // Act
        DynamicPeerTls.Snapshot first = await scenario.RenewAsync(scenario.Baseline);

        // Assert
        Assert.Equal(IssueId(accepted), first.CredentialId);
        Assert.Equal(4, scenario.Transport.Sent.Count);
        Assert.Equal(1, authority.Journal.Commits);

        // Arrange
        scenario.Retire();
        authority.Time.Now = new DateTimeOffset(first.Certificate.NotAfter.ToUniversalTime()).AddDays(-7);
        scenario.PublishProcess(epoch: 5);

        // Act
        DynamicPeerTls.Snapshot second = await scenario.RenewAsync(first.CredentialId!.Value);

        // Assert
        Assert.True(second.CredentialRevision > first.CredentialRevision);
        Assert.Equal(2, authority.Journal.Commits);
        Assert.Equal(6, scenario.Transport.Sent.Count);
    }

    [Fact]
    public async Task EachExpiredReplacementNeedsItsOwnDecisionAndMissingLinkFailsClosed()
    {
        // Arrange
        using Scenario scenario = new();
        RevisionCommand original = await DropBeforeAuthorize(scenario);
        AdvancePastWindow(scenario, original);
        scenario.Transport.BeforeCommit = command =>
        {
            if (command.OperationId != original.OperationId)
            {
                throw new IOException("LostReplacementDispatch");
            }
        };
        await Assert.ThrowsAsync<IOException>(() => scenario.RenewAsync(scenario.Baseline));
        RevisionCommand replacement = scenario.Transport.Sent.Last();
        AdvancePastWindow(scenario, replacement);
        scenario.Transport.FailAfter = null;
        scenario.Transport.BeforeCommit = command =>
        {
            if (command.OperationId != replacement.OperationId)
            {
                throw new IOException("LostSecondReplacementDispatch");
            }
        };

        // Act
        await Assert.ThrowsAsync<IOException>(() => scenario.RenewAsync(scenario.Baseline));

        // Assert
        Assert.Equal(OperationOutcome.Rejected, scenario.Local.Authority.Scenario.State.Results[replacement.OperationId].Outcome);
        Assert.Equal(5, scenario.Transport.Sent.Count);
        Assert.Equal(replacement, scenario.Transport.Sent[3]);
        Assert.Equal(0, scenario.Local.Authority.Journal.Commits);

        // Arrange
        File.Delete(scenario.Local.PathFor($"retry-{original.OperationId:N}"));
        scenario.Reopen(restoreBaseline: true);
        scenario.Transport.BeforeCommit = null;

        // Act
        await Assert.ThrowsAsync<InvalidDataException>(() => scenario.RenewAsync(scenario.Baseline));

        // Assert
        Assert.Equal(5, scenario.Transport.Sent.Count);
        Assert.Equal(0, scenario.Local.Authority.Journal.Commits);
    }

    private static async Task<RevisionCommand> DropBeforeAuthorize(Scenario scenario)
    {
        scenario.Transport.BeforeCommit = _ => throw new IOException("LostDispatch");
        await Assert.ThrowsAsync<IOException>(() => scenario.RenewAsync(scenario.Baseline));
        scenario.Transport.BeforeCommit = null;
        return Assert.Single(scenario.Transport.Sent);
    }

    private static Guid IssueId(RevisionCommand command)
    {
        return command.EnrollmentTrust!.Rotation!.Renewal.Issuance.IssueId;
    }

    private static void AdvancePastWindow(Scenario scenario, RevisionCommand command)
    {
        VoterCredentialAuthorityFixture authority = scenario.Local.Authority;
        authority.Time.Now = command.CreatedAtUtc.AddSeconds(10).AddMilliseconds(1);
        authority.Scenario.State = authority.Scenario.State with { Term = authority.Scenario.State.Term + 1 };
        authority.Publish();
    }
}
