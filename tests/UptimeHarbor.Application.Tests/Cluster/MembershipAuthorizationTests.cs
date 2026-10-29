using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Security;
using Xunit;

namespace UptimeHarbor.Tests.Application.Cluster;

public class MembershipAuthorizationTests
{
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task LocalCookieOrUnconfirmedAccountCannotAuthorizeMembership(bool authenticated, bool enabled, bool confirmed)
    {
        // Arrange
        User user = new(authenticated, enabled);
        Ports ports = new(user.UserId, confirmed, 3);
        AuthorityFixture fixture = new();
        MembershipCoordinator coordinator = new(user, ports, ports, fixture.Gate, ports, fixture.Time);

        // Act
        ServiceResult result = await coordinator.ExecuteAsync(ports.Change.ChangeId, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(0, ports.ProtocolChanges);
        Assert.DoesNotContain(ports.Audit, entry => entry.Outcome == "Committed");
        Assert.All(ports.Audit, entry => Assert.Equal(user.UserId, entry.Actor));
    }

    [Fact]
    public async Task LastVoterCannotBeRemovedEvenByConfirmedAdministrator()
    {
        // Arrange
        User user = new(true, true);
        Ports ports = new(user.UserId, true, 1);
        AuthorityFixture fixture = new();
        MembershipCoordinator coordinator = new(user, ports, ports, fixture.Gate, ports, fixture.Time);

        // Act
        ServiceResult result = await coordinator.ExecuteAsync(ports.Change.ChangeId, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(ServiceErrorType.Conflict, result.Error!.Type);
        Assert.Equal(0, ports.ProtocolChanges);
        Assert.Empty(ports.Audit);
    }

    private class User(bool authenticated, bool enabled) : IUserAccessContext
    {
        public bool IsAuthenticated => authenticated;
        public bool IsAdministrator => true;
        public bool IsEnabled => enabled;
        public Guid UserId { get; } = Guid.NewGuid();
    }

    private class Ports : IClusterAdministrationState, IMembershipProtocol, IClusterAudit
    {
        public AcceptedMembershipChange Change { get; }
        private readonly MembershipSnapshot snapshot;
        public int ProtocolChanges { get; private set; }
        public List<(Guid Actor, string Outcome)> Audit { get; } = [];

        public Ports(Guid actor, bool confirmed, int count)
        {
            Guid target = Guid.NewGuid();
            Uri address = new("https://target.example/internal/raft");
            Change = new(Guid.NewGuid(), actor, target, address, true, 1, confirmed, true, true);
            Dictionary<Guid, Uri> voters = new() { [target] = address };
            foreach (int index in Enumerable.Range(1, count - 1))
            {
                voters.Add(Guid.NewGuid(), new Uri($"https://node{index}.example/internal/raft"));
            }

            snapshot = new(1, voters);
        }

        public ValueTask<AcceptedMembershipChange?> FindAuthorizedChangeAsync(Guid userId, Guid changeId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<AcceptedMembershipChange?>(Change);
        }

        public ValueTask<bool> ConfirmProtocolResultAsync(Guid changeId, long membershipVersion, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Unaccepted result must not be confirmed.");
        }

        public ValueTask<MembershipSnapshot?> ReadAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<MembershipSnapshot?>(snapshot);
        }

        public ValueTask<bool> ChangeAsync(AcceptedMembershipChange change, CancellationToken cancellationToken)
        {
            ProtocolChanges++;
            return ValueTask.FromResult(false);
        }

        public void Membership(Guid userId, Guid nodeId, Guid changeId, string outcome)
        {
            Audit.Add((userId, outcome));
        }
    }
}
