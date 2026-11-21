using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Installation.Renewal;
using Xunit;
using Scenario = UptimeHarbor.Tests.Infrastructure.Installation.Renewal.LocalVoterRenewalTests.Scenario;

namespace UptimeHarbor.Tests.Infrastructure.Installation.Renewal;

public class LocalVoterCredentialRetirementTests
{
    [Fact]
    public async Task SavedIntentSurvivesPreCommitFailureReopenAndTermChange()
    {
        // Arrange
        using Scenario scenario = new(remoteIssuer: false);
        var authority = scenario.Local.Authority;
        await scenario.RunScheduledAsync();
        var rotation = VoterCredentialPolicy.Latest(authority.Scenario.State, authority.SubjectId)!;
        authority.Time.Now = rotation.OverlapUntilUtc!.Value;
        scenario.Transport.BeforeCommit = command =>
        {
            Assert.Equal("RetireOldCredential", command.Action);
            Assert.NotNull(scenario.Local.Files.Read(scenario.Local.Reference, LocalVoterCredentialFiles.RETIRE_STAGE));
            throw new IOException("SimulatedFailureBeforeCommit");
        };

        // Act
        await Assert.ThrowsAsync<IOException>(() => scenario.RunScheduledAsync());
        RevisionCommand first = scenario.Transport.Sent.Last();
        Assert.False(authority.Scenario.State.Results.ContainsKey(first.OperationId));
        authority.Scenario.State = authority.Scenario.State with
        {
            Term = authority.Scenario.State.Term + 1, Index = authority.Scenario.State.Index + 1
        };
        authority.Publish();
        scenario.Transport.BeforeCommit = null;
        scenario.Reopen(restoreBaseline: true);
        Assert.NotNull(await scenario.RunScheduledAsync());
        await scenario.RunScheduledAsync();
        await scenario.RunScheduledAsync();

        // Assert
        Assert.Equal(new[] { first, first }, scenario.Transport.Sent.Where(command => command.Action == "RetireOldCredential"));
        var accepted = VoterCredentialPolicy.Latest(authority.Scenario.State, authority.SubjectId)!;
        Assert.Equal(first.OperationId, accepted.RetireOperationId);
        Assert.Equal(authority.Scenario.State.Term, authority.Scenario.State.Results[first.OperationId].Term);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleVotersRequireWorkingWitnessAndUnchangedEvidence(bool changeEvidence)
    {
        // Arrange
        Witness witness = new();
        using Scenario scenario = new(witness);
        var authority = scenario.Local.Authority;
        await scenario.RunScheduledAsync();
        var rotation = VoterCredentialPolicy.Latest(authority.Scenario.State, authority.SubjectId)!;
        authority.Time.Now = rotation.OverlapUntilUtc!.Value;
        witness.Confirm = () =>
        {
            if (!changeEvidence)
            {
                throw new HttpRequestException("SimulatedWitnessUnavailable");
            }

            authority.Scenario.State = authority.Scenario.State with { Index = authority.Scenario.State.Index + 1 };
            authority.Publish();
        };

        // Act
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scenario.RunScheduledAsync());

        // Assert
        Assert.Equal(authority.IssuerOptions.NodeId, witness.Last);
        Assert.Null(scenario.Local.Files.Read(scenario.Local.Reference, LocalVoterCredentialFiles.RETIRE_STAGE));
        Assert.DoesNotContain(scenario.Transport.Sent, command => command.Action == "RetireOldCredential");

        // Act
        witness.Confirm = () => { };
        await scenario.RunScheduledAsync();

        // Assert
        Assert.Single(scenario.Transport.Sent, command => command.Action == "RetireOldCredential");
    }

    private sealed class Witness : IVoterCredentialHandoffPeer
    {
        public Action Confirm { get; set; } = () => { };
        public Guid Last { get; private set; }

        public Task ConfirmAsync(OperationalNode witness, VoterCredentialHandoff handoff, DynamicPeerTls.Snapshot snapshot,
            RevisionImage image, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Last = witness.NodeId;
            Assert.Equal(handoff.CredentialRevision, snapshot.CredentialRevision);
            Assert.Equal(handoff.Fingerprint, snapshot.Fingerprint);
            Assert.Equal(witness, image.Operations.Nodes[witness.NodeId]);
            Confirm();
            return Task.CompletedTask;
        }
    }
}
