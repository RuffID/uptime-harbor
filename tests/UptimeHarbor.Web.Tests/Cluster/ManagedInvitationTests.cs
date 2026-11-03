using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
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
        User user = new(first.Account.ActorUserId);
        AcceptedAdministratorAccessPolicy policy = new(user, first.Accepted);
        ClusterAdministrationAccess access = new(policy, user, first.Accepted);
        InvitationHttpFixture.MemoryPending work = new();
        Protection protection = new();
        PendingCommandService pending = new(work, user, policy, first.Accepted, protection, new Audit(), new PendingAdmissionGate());
        InvitationAdministration administration = new(access, first.Accepted, pending, work, protection,
            new Transport(first.Dispatcher, first.Options.NodeId), new Login(), user, Options.Create(first.Options), first.Time);
        Guid operationId = Guid.NewGuid();

        // Act
        var result = await administration.IssueAsync(operationId, null, "test-password", token);

        // Assert
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("Committed", result.Data!.OperationStatus);
        Assert.NotNull(result.Data.Code);
        Assert.Equal(OperationOutcome.Committed, first.Accepted.Current!.Results[operationId].Outcome);
        Assert.Equal(3, RevisionCodec.Decode(protection.Unprotect(work.Items[operationId].ProtectedPayload)).Schema);
        Assert.True(first.Installation.CommonOpen);
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
