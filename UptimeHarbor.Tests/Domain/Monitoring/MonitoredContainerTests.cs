using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Tests.Domain.Monitoring;

public class MonitoredContainerTests
{
    [Fact]
    public void RecordSuccessfulCheck_WhenStartedAtIsUtc_SavesStartedAt()
    {
        MonitoredContainer container = CreateContainer();
        DateTimeOffset checkedAtUtc = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset startedAtUtc = checkedAtUtc.AddHours(-2);

        container.RecordSuccessfulCheck("Orders API", startedAtUtc, checkedAtUtc);

        Assert.Equal(startedAtUtc, container.StartedAtUtc);
        Assert.Equal(MonitoringStatus.Available, container.Status);
    }

    [Fact]
    public void RecordSuccessfulCheck_WhenStartedAtIsInFuture_ThrowsArgumentOutOfRangeException()
    {
        MonitoredContainer container = CreateContainer();
        DateTimeOffset checkedAtUtc = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            container.RecordSuccessfulCheck("Orders API", checkedAtUtc.AddSeconds(1), checkedAtUtc));
    }

    [Fact]
    public void RecordSuccessfulCheck_WhenDateHasNonUtcOffset_ThrowsArgumentException()
    {
        MonitoredContainer container = CreateContainer();
        DateTimeOffset checkedAtUtc = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset startedAtLocal = new(2026, 8, 25, 10, 0, 0, TimeSpan.FromHours(7));

        Assert.Throws<ArgumentException>(() =>
            container.RecordSuccessfulCheck("Orders API", startedAtLocal, checkedAtUtc));
    }

    [Fact]
    public void UpdateSettings_WhenEndpointChanges_ClearsStartedAt()
    {
        MonitoredContainer container = CreateContainer();
        DateTimeOffset checkedAtUtc = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
        container.RecordSuccessfulCheck("Orders API", checkedAtUtc.AddHours(-2), checkedAtUtc);

        container.UpdateSettings("https://new.example.com/health", null, true);

        Assert.Null(container.StartedAtUtc);
    }

    private static MonitoredContainer CreateContainer()
    {
        return new MonitoredContainer(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://example.com/health",
            "Test container",
            true);
    }
}
