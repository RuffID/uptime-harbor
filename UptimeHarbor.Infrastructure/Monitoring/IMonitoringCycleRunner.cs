namespace UptimeHarbor.Infrastructure.Monitoring;

public interface IMonitoringCycleRunner
{
    Task RunCycleAsync(CancellationToken cancellationToken = default);
}
