using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using Xunit;

namespace UptimeHarbor.Tests.Application.Cluster.Renewal;

public class VoterCredentialAdmissionTests
{
    [Theory]
    [InlineData("AuthorizeVoterCredential")]
    [InlineData("BindVoterCredential")]
    [InlineData("RetireOldCredential")]
    public async Task SuppliedStampOrSystemSourceCannotOpenProductionAdmission(string action)
    {
        // Arrange
        RenewalScenario scenario = new();
        RevisionStateStore store = new();
        store.Publish(scenario.State);
        Writer writer = new();
        Acceptance acceptance = new();
        RevisionDispatcher dispatcher = new(writer, store, onboarding: acceptance);

        // Act
        Exception? failure = await Record.ExceptionAsync(() => dispatcher.AcceptAsync(scenario.Command(action), scenario.Node,
            TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal("VoterCredentialRenewalAuthorityUnavailable", failure.Message);
        Assert.Equal(0, writer.Calls);
        Assert.Equal(0, acceptance.Calls);
        Assert.Equal(scenario.State, store.Current);
    }

    [Theory]
    [InlineData("AuthorizeVoterCredential")]
    [InlineData("BindVoterCredential")]
    [InlineData("RetireOldCredential")]
    public async Task AcceptedRotationReplayRequiresTypedAdmission(string action)
    {
        RenewalScenario scenario = new();
        RevisionCommand command = scenario.Commit("AuthorizeVoterCredential");
        if (action is "BindVoterCredential" or "RetireOldCredential")
        {
            command = scenario.Commit("BindVoterCredential");
        }

        if (action == "RetireOldCredential")
        {
            scenario.Now = scenario.Rotation.OverlapUntilUtc!.Value;
            command = scenario.Commit(action);
        }

        Assert.Equal(action, command.Action);
        RevisionStateStore store = new();
        store.Publish(scenario.State);
        Writer writer = new();
        Acceptance acceptance = new();
        RevisionDispatcher dispatcher = new(writer, store, onboarding: acceptance);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.AcceptAsync(command, scenario.Node, TestContext.Current.CancellationToken));

        Assert.Equal("VoterCredentialRenewalAuthorityUnavailable", failure.Message);
        Assert.Equal(0, writer.Calls);
        Assert.Equal(0, acceptance.Calls);
        Assert.Same(scenario.State, store.Current);
    }

    private class Writer : IConsensusWriter
    {
        public int Calls { get; private set; }

        public ValueTask<long> ReplicateAsync(ReadOnlyMemory<byte> payload, int commandId, CancellationToken token)
        {
            Calls++;
            throw new InvalidOperationException("UnexpectedReplication");
        }

        public ValueTask<bool> ResignAsync(CancellationToken token)
        {
            throw new InvalidOperationException("UnexpectedResign");
        }
    }

    private class Acceptance : IOnboardingAcceptance
    {
        public int Calls { get; private set; }

        public ValueTask<RevisionCommand> PrepareAsync(RevisionCommand command, CancellationToken token)
        {
            Calls++;
            throw new InvalidOperationException("UnexpectedAcceptance");
        }
    }
}
