using UptimeHarbor.Application.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster;

public class MonotonicClockMonitor(TimeProvider timeProvider, NodeReadiness readiness)
{
    private long last = timeProvider.GetTimestamp();
    private DateTimeOffset utc = timeProvider.GetUtcNow();
    private bool fault;

    public void Sample()
    {
        long now = timeProvider.GetTimestamp();
        DateTimeOffset wall = timeProvider.GetUtcNow();
        TimeSpan elapsed = timeProvider.GetElapsedTime(last, now);
        if (elapsed <= TimeSpan.Zero || elapsed > TimeSpan.FromSeconds(5)
            || (wall - utc - elapsed).Duration() > TimeSpan.FromSeconds(2))
        {
            fault = true;
        }

        last = now;
        utc = wall;
        string reason = "Ready";
        if (fault)
        {
            reason = "ClockUncertain";
        }

        readiness.Report("Clock", !fault, reason, TimeSpan.FromSeconds(5));
    }
}
