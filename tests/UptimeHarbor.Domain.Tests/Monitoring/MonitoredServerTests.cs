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
        // Arrange
        MonitoredServer server = CreateServer();
        DateTimeOffset firstCheck = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);

        // Act
        MonitoringTransition availableTransition = server.RecordSuccessfulCheck("server-01", firstCheck.AddDays(-1), firstCheck);
        server.RecordFailedCheck(firstCheck.AddSeconds(30), 3);
        server.RecordFailedCheck(firstCheck.AddSeconds(60), 3);
        MonitoringTransition unavailableTransition = server.RecordFailedCheck(firstCheck.AddSeconds(90), 3);
        MonitoringTransition recoveryTransition = server.RecordSuccessfulCheck(
            "server-01",
            firstCheck.AddDays(-1),
            firstCheck.AddSeconds(120));

        // Assert
        Assert.Equal(MonitoringTransition.BecameAvailable, availableTransition);
        Assert.Equal(MonitoringTransition.BecameUnavailable, unavailableTransition);
        Assert.Equal(MonitoringTransition.Recovered, recoveryTransition);
        Assert.Equal(MonitoringStatus.Available, server.Status);
        Assert.Equal(0, server.ConsecutiveFailures);
    }

    [Fact]
    public void SuccessfulCheck_ResetsConsecutiveFailures()
    {
        // Arrange
        MonitoredServer server = CreateServer();
        DateTimeOffset checkedAtUtc = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
        server.RecordFailedCheck(checkedAtUtc, 3);
        server.RecordFailedCheck(checkedAtUtc.AddSeconds(30), 3);

        // Act
        server.RecordSuccessfulCheck("server-01", checkedAtUtc.AddDays(-1), checkedAtUtc.AddSeconds(60));

        // Assert
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
        // Arrange
        MonitoredServer server = CreateServer();
        DateTimeOffset checkedAtUtc = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
        server.RecordSuccessfulCheck("server-01", checkedAtUtc.AddDays(-1), checkedAtUtc);

        // Act
        server.UpdateSettings("Renamed server", "https://new.example.com/health", "updated", true);

        // Assert
        Assert.Equal("Renamed server", server.Name);
        Assert.Null(server.LastKnownName);
        Assert.Null(server.LastCheckedAtUtc);
        Assert.Null(server.LastSuccessfulCheckAtUtc);
        Assert.Null(server.StartedAtUtc);
        Assert.Equal(MonitoringStatus.Unknown, server.Status);
    }

    [Fact]
    public void AdministrativeAndMonitoringChanges_IncrementConcurrencyVersion()
    {
        // Arrange
        MonitoredServer server = CreateServer();

        // Act
        server.UpdateSettings(server.Name, "https://new.example.com/health", null, true);
        long afterAdministration = server.Version;
        server.RecordSuccessfulCheck(
            "server-01",
            new DateTimeOffset(2026, 8, 29, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 30, 9, 0, 0, TimeSpan.Zero));

        // Assert
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

    [Fact]
    public void SuccessfulCheck_UpdatesBootTimeAfterRestartAndFailurePreservesIt()
    {
        // Arrange
        MonitoredServer server = CreateServer();
        DateTimeOffset checkedAtUtc = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
        server.RecordSuccessfulCheck("server-01", checkedAtUtc.AddDays(-1), checkedAtUtc);

        // Act
        server.RecordSuccessfulCheck("server-01", checkedAtUtc.AddMinutes(1), checkedAtUtc.AddMinutes(2));
        server.RecordFailedCheck(checkedAtUtc.AddMinutes(3), 3);

        // Assert
        Assert.Equal(checkedAtUtc.AddMinutes(1), server.StartedAtUtc);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(7, -60)]
    public void SuccessfulCheck_WhenBootTimeIsInvalid_DoesNotChangeState(int offsetHours, int seconds)
    {
        // Arrange
        MonitoredServer server = CreateServer();
        DateTimeOffset checkedAtUtc = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset startedAtUtc = checkedAtUtc.AddSeconds(seconds).ToOffset(TimeSpan.FromHours(offsetHours));

        // Act
        Exception? error = Record.Exception(() => server.RecordSuccessfulCheck("server-01", startedAtUtc, checkedAtUtc));

        // Assert
        Assert.IsAssignableFrom<ArgumentException>(error);
        Assert.Null(server.StartedAtUtc);
        Assert.Null(server.LastKnownName);
        Assert.Null(server.LastCheckedAtUtc);
        Assert.Equal(MonitoringStatus.Unknown, server.Status);
        Assert.Equal(0, server.Version);
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
