using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using Xunit;

namespace UptimeHarbor.Tests.Application.Cluster;

public class ReadinessAndFailureWindowTests
{
    [Fact]
    public void LeaderHealthCannotReportReadyAfterMajorityTokenIsCancelled()
    {
        // Arrange
        AuthorityFixture fixture = new();
        Assert.True(fixture.Readiness.CanSend(fixture));

        // Act
        fixture.Majority.Cancel();
        bool ready = fixture.Readiness.CanSend(fixture);

        // Assert
        Assert.False(ready);
    }

    [Fact]
    public void DatabaseRepairDoesNotReusePreFailureCatchupProof()
    {
        // Arrange
        AuthorityFixture fixture = new();
        Assert.True(fixture.Readiness.CanWork(fixture));
        long oldVersion = fixture.Readiness.RecoveryVersion;

        // Act
        fixture.Readiness.Report("Database", false, "DatabaseUnavailable", TimeSpan.FromSeconds(10));
        bool duringFault = fixture.Readiness.CanWork(fixture);
        fixture.Readiness.Report("Database", true, "Ready", TimeSpan.FromSeconds(10));
        bool afterRepair = fixture.Readiness.CanWork(fixture);
        bool staleProof = fixture.Readiness.ConfirmRecovery(oldVersion, fixture.Accepted, fixture.Consensus!);
        bool freshProof = fixture.Readiness.ConfirmRecovery(fixture.Readiness.RecoveryVersion,
            fixture.Accepted, fixture.Consensus!);

        // Assert
        Assert.False(duringFault);
        Assert.False(afterRepair);
        Assert.False(staleProof);
        Assert.True(freshProof);
        Assert.True(fixture.Readiness.CanWork(fixture));
    }

    [Fact]
    public void UnknownAccountAndStaleHealthRemainClosed()
    {
        // Arrange
        AuthorityFixture fixture = new();
        fixture.Readiness.Report("Telegram", true, "Ready", TimeSpan.FromSeconds(90));
        fixture.Accepted = fixture.Accepted with { AccountStateConfirmed = false };

        // Act
        bool allowed = fixture.Readiness.CanSend(fixture);
        fixture.Time.Advance(90);
        HealthResult health = fixture.Readiness.Get("Telegram");

        // Assert
        Assert.False(allowed);
        Assert.Equal("Unknown", health.Status);
        Assert.Equal("Stale", health.Reason);
        Assert.Equal(TimeSpan.FromSeconds(90), health.Age);
    }

    [Fact]
    public void FailuresAndMinorityCannotExtendOrBypassThirtySecondWindow()
    {
        // Arrange
        ControlledTime time = new();
        LeaderFailureWindow window = new(time);
        Guid[] nodes = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        HashSet<Guid> voters = [.. nodes];
        window.ObserveTerm(1, 1, nodes[0]);
        PeerHealthSnapshot ready = Health(nodes[0], nodes[0], true, 0);
        window.ObserveAuthenticated(ready);
        time.Advance(29);

        // Act
        window.ObserveAuthenticated(Health(nodes[1], nodes[0], false, 29) with { ConfirmedPeers = voters });
        window.ObserveAuthenticated(Health(nodes[2], nodes[0], false, 29) with { ConfirmedPeers = voters });
        bool early = window.ShouldRelinquish(voters, nodes[3], false);
        time.Advance(1);
        window.ObserveAuthenticated(Health(nodes[1], nodes[0], false, 30) with { ConfirmedPeers = voters });
        bool minority = window.ShouldRelinquish(voters, nodes[3], false);
        window.ObserveAuthenticated(Health(nodes[2], nodes[0], false, 30) with { ConfirmedPeers = voters });
        bool majority = window.ShouldRelinquish(voters, nodes[3], false);

        // Assert
        Assert.False(early);
        Assert.False(minority);
        Assert.True(majority);
        Assert.Equal(30, window.SecondsWithoutReady);
        Assert.True(window.RejectLeaderRpc(nodes[0], 1));
        Assert.False(window.RejectLeaderRpc(nodes[0], 2));
    }

    [Fact]
    public void AReadyLeaderAndFreshLocalContradictionPreventRelinquish()
    {
        // Arrange
        ControlledTime time = new();
        LeaderFailureWindow window = new(time);
        Guid leader = Guid.NewGuid();
        Guid local = Guid.NewGuid();
        HashSet<Guid> voters = [leader, local, Guid.NewGuid()];
        window.ObserveTerm(1, 1, leader);
        time.Advance(31);
        window.ObserveAuthenticated(Health(leader, leader, true, 0));

        // Act
        bool allowed = window.ShouldRelinquish(voters, local, false);

        // Assert
        Assert.False(allowed);
        Assert.Equal(0, window.SecondsWithoutReady);
    }

    private static PeerHealthSnapshot Health(Guid node, Guid leader, bool ready, double seconds)
    {
        return new(node, Guid.NewGuid(), 1, leader, 1, 10, 10, ready, seconds,
            new Dictionary<string, HealthResult>());
    }

    [Fact]
    public void AsymmetricWitnessesAndStaleReportsDoNotFormHealthQuorum()
    {
        // Arrange
        ControlledTime time = new();
        LeaderFailureWindow window = new(time);
        Guid leader = Guid.NewGuid();
        Guid local = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        HashSet<Guid> voters = [leader, local, other];
        window.ObserveTerm(1, 1, leader);
        time.Advance(30);
        window.ObserveAuthenticated(Health(other, leader, false, 30));

        // Act
        bool asymmetric = window.ShouldRelinquish(voters, local, false);
        window.ObserveAuthenticated(Health(other, leader, false, 30) with { ConfirmedPeers = voters });
        time.Advance(10);
        bool stale = window.ShouldRelinquish(voters, local, false);

        // Assert
        Assert.False(asymmetric);
        Assert.False(stale);
        Assert.False(window.RejectLeaderRpc(leader, 1));
    }
}
