using UptimeHarbor.Domain.Monitoring;
using Xunit;

namespace UptimeHarbor.Tests.Domain.Monitoring;

public class MonitoredServerTests
{
    [Fact]
    public void Constructor_NormalizesConfiguredName()
    {
        MonitoredServer server = new(
            Guid.NewGuid(),
            "  Production server  ",
            "https://example.com/health",
            null,
            true);

        Assert.Equal("Production server", server.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WhenConfiguredNameIsEmpty_ThrowsArgumentException(string name)
    {
        Assert.Throws<ArgumentException>(() => new MonitoredServer(
            Guid.NewGuid(),
            name,
            "https://example.com/health",
            null,
            true));
    }

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
        Assert.NotNull(server.CurrentIncidentId);

        MonitoringTransition transition = server.SetMonitoringEnabled(false);

        Assert.Equal(MonitoringTransition.MonitoringDisabled, transition);
        Assert.Equal(MonitoringStatus.Unknown, server.Status);
        Assert.Equal(0, server.ConsecutiveFailures);
        Assert.Null(server.CurrentIncidentId);
    }

    [Fact]
    public void UpdateSettings_WhenEndpointChanges_ClearsEndpointState()
    {
        MonitoredServer server = CreateServer();
        DateTimeOffset checkedAtUtc = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
        server.RecordSuccessfulCheck("server-01", checkedAtUtc);

        server.UpdateSettings("Renamed server", "https://new.example.com/health", "updated", true);

        Assert.Equal("Renamed server", server.Name);
        Assert.Null(server.LastKnownName);
        Assert.Null(server.LastCheckedAtUtc);
        Assert.Null(server.LastSuccessfulCheckAtUtc);
        Assert.Equal(MonitoringStatus.Unknown, server.Status);
    }

    [Fact]
    public void AdministrativeAndMonitoringChanges_IncrementConcurrencyVersion()
    {
        MonitoredServer server = CreateServer();

        server.UpdateSettings(server.Name, "https://new.example.com/health", null, true);
        long afterAdministration = server.Version;
        server.RecordSuccessfulCheck(
            "server-01",
            new DateTimeOffset(2026, 8, 30, 9, 0, 0, TimeSpan.Zero));

        Assert.Equal(1, afterAdministration);
        Assert.Equal(2, server.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://example.com/health")]
    [InlineData("not-a-url")]
    public void Constructor_WhenEndpointIsInvalid_ThrowsArgumentException(string endpointUrl)
    {
        Assert.Throws<ArgumentException>(() =>
            new MonitoredServer(Guid.NewGuid(), "Test server", endpointUrl, null, true));
    }

    private static MonitoredServer CreateServer()
    {
        return new MonitoredServer(
            Guid.NewGuid(),
            "Test server",
            "https://example.com/health",
            "Test server",
            true);
    }
}
