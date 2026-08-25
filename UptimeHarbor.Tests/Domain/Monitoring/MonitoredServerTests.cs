using UptimeHarbor.Domain.Monitoring;
using Xunit;

namespace UptimeHarbor.Tests.Domain.Monitoring;

public class MonitoredServerTests
{
    [Fact]
    public void Checks_TransitionFromUnknownToAvailableToUnavailableAndBack()
    {
        MonitoredServer server = CreateServer();
        DateTimeOffset firstCheck = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);

        MonitoringTransition availableTransition = server.RecordSuccessfulCheck("server-01", firstCheck);
        server.RecordFailedCheck(firstCheck.AddSeconds(30), 3);
        server.RecordFailedCheck(firstCheck.AddSeconds(60), 3);
        MonitoringTransition unavailableTransition = server.RecordFailedCheck(firstCheck.AddSeconds(90), 3);
        MonitoringTransition recoveryTransition = server.RecordSuccessfulCheck(
            "server-01",
            firstCheck.AddSeconds(120));

        Assert.Equal(MonitoringTransition.BecameAvailable, availableTransition);
        Assert.Equal(MonitoringTransition.BecameUnavailable, unavailableTransition);
        Assert.Equal(MonitoringTransition.Recovered, recoveryTransition);
        Assert.Equal(MonitoringStatus.Available, server.Status);
        Assert.Equal(0, server.ConsecutiveFailures);
    }

    [Fact]
    public void SuccessfulCheck_ResetsConsecutiveFailures()
    {
        MonitoredServer server = CreateServer();
        DateTimeOffset checkedAtUtc = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
        server.RecordFailedCheck(checkedAtUtc, 3);
        server.RecordFailedCheck(checkedAtUtc.AddSeconds(30), 3);

        server.RecordSuccessfulCheck("server-01", checkedAtUtc.AddSeconds(60));

        Assert.Equal(0, server.ConsecutiveFailures);
        Assert.Equal(MonitoringStatus.Available, server.Status);
    }

    [Fact]
    public void DisableMonitoring_ClosesIncidentWithoutRecovery()
    {
        MonitoredServer server = CreateServer();
        DateTimeOffset checkedAtUtc = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
        server.RecordFailedCheck(checkedAtUtc, 1);
        server.MarkOutageNotificationSent();

        MonitoringTransition transition = server.SetMonitoringEnabled(false);

        Assert.Equal(MonitoringTransition.MonitoringDisabled, transition);
        Assert.Equal(MonitoringStatus.Unknown, server.Status);
        Assert.Equal(0, server.ConsecutiveFailures);
        Assert.False(server.OutageNotificationSent);
    }

    [Fact]
    public void UpdateSettings_WhenEndpointChanges_ClearsEndpointState()
    {
        MonitoredServer server = CreateServer();
        DateTimeOffset checkedAtUtc = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
        server.RecordSuccessfulCheck("server-01", checkedAtUtc);

        server.UpdateSettings("https://new.example.com/health", "updated", true);

        Assert.Null(server.LastKnownName);
        Assert.Null(server.LastCheckedAtUtc);
        Assert.Null(server.LastSuccessfulCheckAtUtc);
        Assert.Equal(MonitoringStatus.Unknown, server.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://example.com/health")]
    [InlineData("not-a-url")]
    public void Constructor_WhenEndpointIsInvalid_ThrowsArgumentException(string endpointUrl)
    {
        Assert.Throws<ArgumentException>(() =>
            new MonitoredServer(Guid.NewGuid(), endpointUrl, null, true));
    }

    private static MonitoredServer CreateServer()
    {
        return new MonitoredServer(
            Guid.NewGuid(),
            "https://example.com/health",
            "Test server",
            true);
    }
}
