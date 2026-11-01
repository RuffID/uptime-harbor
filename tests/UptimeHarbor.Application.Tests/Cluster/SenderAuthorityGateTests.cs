using Xunit;

namespace UptimeHarbor.Tests.Application.Cluster;

public class SenderAuthorityGateTests
{
    [Fact]
    public async Task LeaderAndDrainAloneNeverAuthorizeTransport()
    {
        // Arrange
        AuthorityFixture fixture = new();
        fixture.TickSeconds(12);
        int sent = 0;

        // Act
        bool allowed = await fixture.Gate.ExecuteAttemptAsync(Guid.NewGuid(), _ =>
        {
            sent++;
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(allowed);
        Assert.Equal(0, sent);
        Assert.Equal(0, fixture.Attempts);
    }

    [Fact]
    public async Task SingletonRequiresRenewalAndAppliedAttemptBeforeTransport()
    {
        // Arrange
        AuthorityFixture fixture = new();
        int sent = 0;

        // Act
        bool beforeDrain = await fixture.Gate.RenewAsync(TestContext.Current.CancellationToken);
        fixture.TickSeconds(12);
        bool renewed = await fixture.Gate.RenewAsync(TestContext.Current.CancellationToken);
        bool allowed = await fixture.Gate.ExecuteAttemptAsync(Guid.NewGuid(), _ =>
        {
            sent++;
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(beforeDrain);
        Assert.True(renewed);
        Assert.True(allowed);
        Assert.Equal(1, sent);
        Assert.Equal(1, fixture.Attempts);
    }

    [Theory]
    [InlineData("majority")]
    [InlineData("database")]
    [InlineData("catchup")]
    [InlineData("generation")]
    [InlineData("restart")]
    [InlineData("clock")]
    [InlineData("pause")]
    [InlineData("expiry")]
    public async Task LossOfAnyConditionPreventsNewAttempt(string failure)
    {
        // Arrange
        AuthorityFixture fixture = new();
        fixture.TickSeconds(12);
        Assert.True(await fixture.Gate.RenewAsync(TestContext.Current.CancellationToken));
        int sent = 0;
        switch (failure)
        {
            case "majority":
                fixture.Majority.Cancel();
                break;
            case "database":
                fixture.Readiness.Report("Database", false, "DatabaseUnavailable", TimeSpan.FromSeconds(10));
                break;
            case "catchup":
                fixture.Accepted = fixture.Accepted with { CaughtUp = false };
                break;
            case "generation":
                fixture.Consensus = fixture.Consensus! with { Generation = 2 };
                break;
            case "restart":
                fixture.Consensus = fixture.Consensus! with { IsLeader = false };
                fixture.Gate.Tick();
                fixture.Consensus = fixture.Consensus with { IsLeader = true, Term = 2 };
                break;
            case "clock":
                fixture.Time.JumpUtc(-100);
                break;
            case "pause":
                fixture.Time.Advance(6);
                break;
            case "expiry":
                fixture.TickSeconds(10);
                break;
        }

        // Act
        bool allowed = await fixture.Gate.ExecuteAttemptAsync(Guid.NewGuid(), _ =>
        {
            sent++;
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(allowed);
        Assert.Equal(0, sent);
    }

    [Theory]
    [InlineData(10, 0)]
    [InlineData(0, 2.001)]
    public async Task LateProofDoesNotExtendDeadline(double renewalDelay, double attemptDelay)
    {
        // Arrange
        AuthorityFixture fixture = new() { RenewalDelay = renewalDelay, AttemptDelay = attemptDelay };
        fixture.TickSeconds(12);
        int sent = 0;

        // Act
        bool renewed = await fixture.Gate.RenewAsync(TestContext.Current.CancellationToken);
        bool allowed = await fixture.Gate.ExecuteAttemptAsync(Guid.NewGuid(), _ =>
        {
            sent++;
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(allowed);
        Assert.Equal(0, sent);
        Assert.Equal(renewalDelay == 0, renewed);
    }

    [Fact]
    public async Task FailedRenewalCannotPreserveOldAuthority()
    {
        // Arrange
        AuthorityFixture fixture = new();
        fixture.TickSeconds(12);
        Assert.True(await fixture.Gate.RenewAsync(TestContext.Current.CancellationToken));
        fixture.AcceptRenewal = false;

        // Act
        bool renewed = await fixture.Gate.RenewAsync(TestContext.Current.CancellationToken);
        bool allowed = await fixture.Gate.ExecuteAttemptAsync(Guid.NewGuid(), _ => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.False(renewed);
        Assert.False(allowed);
    }

    [Fact]
    public async Task CloseCancelsAnAlreadyAdmittedRequest()
    {
        // Arrange
        AuthorityFixture fixture = new();
        fixture.TickSeconds(12);
        Assert.True(await fixture.Gate.RenewAsync(TestContext.Current.CancellationToken));
        bool canceled = false;

        // Act
        bool started = await fixture.Gate.ExecuteAttemptAsync(Guid.NewGuid(), cancellation =>
        {
            fixture.Gate.Close();
            canceled = cancellation.IsCancellationRequested;
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(started);
        Assert.True(canceled);
    }

    [Fact]
    public async Task AuthorityExpiryCancelsInFlightScopeAndDeniesAnotherAttempt()
    {
        // Arrange
        AuthorityFixture fixture = new();
        fixture.TickSeconds(12);
        Assert.True(await fixture.Gate.RenewAsync(TestContext.Current.CancellationToken));
        bool canceled = false;

        // Act
        bool started = await fixture.Gate.ExecuteAttemptAsync(Guid.NewGuid(), cancellation =>
        {
            fixture.TickSeconds(10);
            canceled = cancellation.IsCancellationRequested;
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
        bool another = await fixture.Gate.ExecuteAttemptAsync(Guid.NewGuid(), _ => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.True(started);
        Assert.True(canceled);
        Assert.False(another);
    }
}
