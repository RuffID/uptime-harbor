using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Security;
using Xunit;

namespace UptimeHarbor.Tests.Application.Cluster;

public class AcceptedAdministratorTests
{
    [Theory]
    [InlineData(false, true, false, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, false, true)]
    public void OnlyAuthenticatedActiveAcceptedAdministratorIsAllowed(bool authenticated, bool enabled, bool deleted, bool accepted)
    {
        // Arrange
        Guid id = Guid.NewGuid();
        User user = new(id, authenticated);
        Reader reader = new();
        if (accepted)
        {
            reader.Account = new AcceptedAccount(id, 4, enabled, true, deleted);
        }

        AcceptedAdministratorAccessPolicy policy = new(user, reader);

        // Act
        ServiceError? result = policy.GetAccessError();

        // Assert
        Assert.Equal(authenticated && enabled && !deleted && accepted, result is null);
    }

    [Fact]
    public void PendingLocalAccountIsNotVisibleAndApplyFaultClosesAccess()
    {
        // Arrange
        RevisionScenario scenario = new();
        RevisionStateStore state = new();
        User user = new(scenario.Actor, true);
        AcceptedAdministratorAccessPolicy policy = new(user, state);

        // Act
        ServiceError? before = policy.GetAccessError();
        state.Publish(scenario.State);
        ServiceError? applied = policy.GetAccessError();
        state.Invalidate();
        ServiceError? failed = policy.GetAccessError();

        // Assert
        Assert.NotNull(before);
        Assert.Null(applied);
        Assert.NotNull(failed);
    }

    private class Reader : IAcceptedAccountReader
    {
        public AcceptedAccount? Account { get; set; }
        public AcceptedAccount? Find(Guid userId)
        {
            return Account;
        }
    }

    private class User(Guid id, bool authenticated) : IUserAccessContext
    {
        public bool IsAuthenticated => authenticated;
        public bool IsAdministrator => false;
        public bool IsEnabled => false;
        public Guid UserId => id;
    }
}
