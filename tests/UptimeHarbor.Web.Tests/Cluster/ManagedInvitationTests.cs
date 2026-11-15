using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Identity;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class ManagedInvitationTests
{
    [Fact]
    public async Task ProductionAdministratorIssuesInvitationAfterNewFirstEnableOnTrustSchema()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new();
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        Scenario scenario = new(first, first.Accepted, first.Dispatcher, first.Account.ActorUserId);
        Guid operationId = Guid.NewGuid();

        // Act
        ServiceResult<InvitationResponse> result = await scenario.Administration.IssueAsync(operationId, null, "test-password", token);

        // Assert
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("Committed", result.Data!.OperationStatus);
        Assert.NotNull(result.Data.Code);
        Assert.Equal(OperationOutcome.Committed, first.Accepted.Current!.Results[operationId].Outcome);
        Assert.Equal(3, RevisionCodec.Decode(scenario.Protection.Unprotect(scenario.Work.Items[operationId].ProtectedPayload)).Schema);
        Assert.True(first.Installation.CommonOpen);
    }

    [Fact]
    public async Task LostInvitationResponseKeepsOneAcceptedOperationAndNeverReturnsCodeOnRetry()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        Scenario scenario = new(first, first.Accepted, first.Dispatcher, first.Account.ActorUserId);
        Guid operationId = Guid.NewGuid();
        first.Native.LoseResponse = true;

        // Act
        ServiceResult<InvitationResponse> lost = await scenario.Administration.IssueAsync(operationId, null, "test-password", token);
        string payload = scenario.Work.Items[operationId].ProtectedPayload;
        int replications = first.Native.Replications;
        ServiceResult<InvitationResponse> retry = await scenario.Administration.IssueAsync(operationId, null, "test-password", token);

        // Assert
        Assert.True(lost.Success, lost.Error?.Message);
        Assert.Equal("Committed", lost.Data!.OperationStatus);
        Assert.Null(lost.Data.Code);
        Assert.True(retry.Success, retry.Error?.Message);
        Assert.Null(retry.Data!.Code);
        Assert.Equal(1, first.Native.DroppedResponses);
        Assert.Equal(replications, first.Native.Replications);
        Assert.Single(first.Accepted.Current!.Onboarding!.Invitations);
        Assert.Equal(payload, scenario.Work.Items[operationId].ProtectedPayload);
        Assert.Equal(3, RevisionCodec.Decode(scenario.Protection.Unprotect(payload)).Schema);

        // Act
        Guid replacementId = Guid.NewGuid();
        ServiceResult<InvitationResponse> replacement = await scenario.Administration.IssueAsync(replacementId, operationId, "test-password", token);

        // Assert
        Assert.True(replacement.Success, replacement.Error?.Message);
        Assert.Equal("Committed", replacement.Data!.OperationStatus);
        Assert.NotNull(replacement.Data.Code);
        Assert.Equal(InvitationStatus.Revoked, first.Accepted.Current.Onboarding.Invitations[operationId].Status);
        Assert.Equal(InvitationStatus.Unused, first.Accepted.Current.Onboarding.Invitations[replacementId].Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetupIdentityOrWrongCommonPasswordCannotIssueInvitation(bool setupIdentity)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new();
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        Guid actor = first.Account.ActorUserId;
        string password = "wrong-password";
        if (setupIdentity)
        {
            actor = first.Store.Account.Id;
            password = "test-password";
        }

        Scenario scenario = new(first, first.Accepted, first.Dispatcher, actor);
        int replications = first.Native.Replications;

        // Act
        ServiceResult<InvitationResponse> denied = await scenario.Administration.IssueAsync(Guid.NewGuid(), null, password, token);

        // Assert
        Assert.False(denied.Success);
        Assert.Empty(scenario.Work.Items);
        Assert.True(first.Accepted.Current!.Onboarding?.Invitations.IsEmpty ?? true);
        Assert.Equal(replications, first.Native.Replications);
    }

    [Fact]
    public async Task RestartKeepsInvitationAndIssuesReplacementOnSchemaThreeAfterNewEnable()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ManagedBootstrapFixture first = new(durableRestart: true);
        await EnrollmentGatewayTests.ReadyAsync(first, token);
        Scenario initial = new(first, first.Accepted, first.Dispatcher, first.Account.ActorUserId);
        Guid originalId = Guid.NewGuid();
        ServiceResult<InvitationResponse> original = await initial.Administration.IssueAsync(originalId, null, "test-password", token);
        Assert.True(original.Success);
        Assert.NotNull(original.Data!.Code);
        Guid genesis = first.Accepted.Current!.OnboardingContracts!.Genesis!.GenesisId;
        await first.StopStorageAsync();
        await using ManagedRestartFixture restart = new(first);
        await restart.RestoreAsync(token);
        first.Time.Now = first.Time.Now.AddMinutes(3);
        await restart.RefreshAsync(token);
        for (int step = 0; step < 5; step++)
        {
            await restart.Driver.TickAsync(token);
        }

        Assert.True(restart.Installation.CommonOpen);
        Scenario restored = new(first, restart.Accepted, restart.Dispatcher, first.Account.ActorUserId);

        // Act
        ServiceResult<InvitationResponse> retry = await restored.Administration.IssueAsync(originalId, null, "test-password", token);
        Guid nextId = Guid.NewGuid();
        ServiceResult<InvitationResponse> next = await restored.Administration.IssueAsync(nextId, originalId, "test-password", token);

        // Assert
        Assert.True(retry.Success);
        Assert.Null(retry.Data!.Code);
        Assert.True(next.Success, next.Error?.Message);
        Assert.Equal("Committed", next.Data!.OperationStatus);
        Assert.NotNull(next.Data.Code);
        Assert.NotEqual(original.Data.Code, next.Data.Code);
        Assert.Equal(3, RevisionCodec.Decode(restored.Protection.Unprotect(restored.Work.Items[nextId].ProtectedPayload)).Schema);
        Assert.Equal(genesis, restart.Accepted.Current!.OnboardingContracts!.Genesis!.GenesisId);
        Assert.Equal(first.Account.ActorUserId, Assert.Single(restart.Accepted.Current.Values).Key);
        Assert.True(restart.Enable.Allows());
    }

    private class Scenario
    {
        public InvitationHttpFixture.MemoryPending Work { get; } = new();
        public Protection Protection { get; } = new();
        public InvitationAdministration Administration { get; }

        public Scenario(ManagedBootstrapFixture first, RevisionStateStore state, RevisionDispatcher dispatcher, Guid actor)
        {
            User user = new(actor);
            AcceptedAdministratorAccessPolicy policy = new(user, state);
            ClusterAdministrationAccess access = new(policy, user, state);
            PendingCommandService pending = new(Work, user, policy, state, Protection, new Audit(), new PendingAdmissionGate());
            Administration = new(access, state, pending, Work, Protection, new Transport(dispatcher, first.Options.NodeId),
                new Login(), user, Options.Create(first.Options), first.Time);
        }
    }

    private class User(Guid id) : IUserAccessContext
    {
        public Guid UserId => id;
        public bool IsAuthenticated => true;
        public bool IsEnabled => true;
        public bool IsAdministrator => true;
    }

    private class Login : ILocalAccountLogin
    {
        public Task<bool> CheckAsync(SharedAccount account, string password, CancellationToken token)
        {
            return Task.FromResult(password == "test-password");
        }
    }

    private class Protection : IRevisionPayloadProtection
    {
        public string Protect(ReadOnlySpan<byte> payload)
        {
            return Convert.ToBase64String(payload);
        }

        public byte[] Unprotect(string payload)
        {
            return Convert.FromBase64String(payload);
        }
    }

    private class Audit : IRevisionAudit
    {
        public void Record(Guid operationId, Guid actorUserId, Guid nodeId, string outcome)
        {
        }
    }

    private class Transport(RevisionDispatcher dispatcher, Guid nodeId) : IRevisionTransport
    {
        public async Task<IReadOnlyList<CommandResult>> SendAsync(IReadOnlyList<RevisionCommand> commands, CancellationToken token)
        {
            List<CommandResult> results = [];
            foreach (RevisionCommand command in commands)
            {
                results.Add(await dispatcher.AcceptAsync(command, nodeId, token));
            }

            return results;
        }
    }
}
