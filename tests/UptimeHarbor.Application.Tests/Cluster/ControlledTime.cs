namespace UptimeHarbor.Tests.Application.Cluster;

internal class ControlledTime : TimeProvider
{
    private long timestamp;
    private DateTimeOffset utc = new(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        return timestamp;
    }

    public override DateTimeOffset GetUtcNow()
    {
        return utc;
    }

    public void Advance(double seconds)
    {
        TimeSpan elapsed = TimeSpan.FromSeconds(seconds);
        timestamp += elapsed.Ticks;
        utc += elapsed;
    }

    public void JumpUtc(double seconds)
    {
        utc += TimeSpan.FromSeconds(seconds);
    }
}
