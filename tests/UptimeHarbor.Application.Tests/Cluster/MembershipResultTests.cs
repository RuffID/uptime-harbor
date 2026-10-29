using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Security;
using Xunit;

namespace UptimeHarbor.Tests.Application.Cluster;

public class MembershipResultTests
{
    [Fact]
    public async Task AcceptedAccountDisabledDuringDrainCannotChangeMembership()
    {
        // Arrange
        Ports ports = new(true, true);
        AuthorityFixture fixture = new();
        ChangingAccount accounts = new(ports.UserId);
        MembershipCoordinator coordinator = new(ports, ports, ports, fixture.Gate, ports, new ImmediateDelayTime(),
            new AcceptedAdministratorAccessPolicy(ports, accounts));

        // Act
        ServiceResult result = await coordinator.ExecuteAsync(ports.Change.ChangeId, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(ServiceErrorType.Forbidden, result.Error!.Type);
        Assert.Equal("Denied", Assert.Single(ports.Audit).Outcome);
    }

    private class ChangingAccount(Guid id) : IAcceptedAccountReader
    {
        private int reads;

        public AcceptedAccount? Find(Guid userId)
        {
            reads++;
            return new AcceptedAccount(id, reads, reads == 1, true, false);
        }
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public async Task SuccessRequiresProtocolResultAndAcceptedReceipt(bool committed, bool receipt, bool success)
    {
        // Arrange
        Ports ports = new(committed, receipt);
        AuthorityFixture fixture = new();
        MembershipCoordinator coordinator = new(ports, ports, ports, fixture.Gate, ports, new ImmediateDelayTime(),
            new AdministratorAccessPolicy(ports));

        // Act
        ServiceResult result = await coordinator.ExecuteAsync(ports.Change.ChangeId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(success, result.Success);
        var entry = Assert.Single(ports.Audit);
        Assert.Equal(ports.UserId, entry.Actor);
        Assert.Equal(ports.Change.TargetNodeId, entry.Node);
        Assert.Equal(success, entry.Outcome == "Committed");
        Assert.False(await fixture.Gate.RenewAsync(TestContext.Current.CancellationToken));
    }

    private class ImmediateDelayTime : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // Проверяется результат coordinator; длительность drain покрывается отдельным gate-контрактом.
            return new Timer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    private class Ports : IUserAccessContext, IClusterAdministrationState, IMembershipProtocol, IClusterAudit
    {
        private readonly bool committed;
        private readonly bool receipt;
        private bool changed;
        public Guid UserId { get; } = Guid.NewGuid();
        public bool IsAuthenticated => true;
        public bool IsEnabled => true;
        public bool IsAdministrator => true;
        public AcceptedMembershipChange Change { get; }
        public List<(Guid Actor, Guid Node, string Outcome)> Audit { get; } = [];
        private readonly Guid other = Guid.NewGuid();

        public Ports(bool committed, bool receipt)
        {
            this.committed = committed;
            this.receipt = receipt;
            Change = new(Guid.NewGuid(), UserId, Guid.NewGuid(), new Uri("https://removed.example/internal/raft"),
                true, 1, true, true, true);
        }

        public ValueTask<AcceptedMembershipChange?> FindAuthorizedChangeAsync(Guid userId, Guid changeId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<AcceptedMembershipChange?>(Change);
        }

        public ValueTask<bool> ConfirmProtocolResultAsync(Guid changeId, long membershipVersion, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(receipt);
        }

        public ValueTask<MembershipSnapshot?> ReadAsync(CancellationToken cancellationToken)
        {
            Dictionary<Guid, Uri> voters = new() { [other] = new Uri("https://other.example/internal/raft") };
            long version = 2;
            if (!changed)
            {
                version = 1;
                voters.Add(Change.TargetNodeId, Change.Address);
            }

            return ValueTask.FromResult<MembershipSnapshot?>(new(version, voters));
        }

        public ValueTask<bool> ChangeAsync(AcceptedMembershipChange change, CancellationToken cancellationToken)
        {
            changed = committed;
            return ValueTask.FromResult(committed);
        }

        public void Membership(Guid userId, Guid nodeId, Guid changeId, string outcome)
        {
            Audit.Add((userId, nodeId, outcome));
        }
    }
}
