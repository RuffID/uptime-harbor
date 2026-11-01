using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Monitoring;

namespace UptimeHarbor.Infrastructure.Cluster;

public class MonitoringProgressWatchdog(NodeReadiness readiness, IOptions<MonitoringOptions> options)
{
    public void RecordCompletedCycle()
    {
        int seconds = Math.Max(2 * options.Value.IntervalSeconds + options.Value.TimeoutSeconds, 30);
        readiness.Report("Monitoring", true, "Ready", TimeSpan.FromSeconds(seconds));
    }
}
