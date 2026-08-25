namespace UptimeHarbor.Domain.Monitoring;

public class MonitoredServer : MonitoredObject
{
    private MonitoredServer()
    {
    }

    public MonitoredServer(
        Guid id,
        string endpointUrl,
        string? description,
        bool isMonitoringEnabled)
        : base(id, endpointUrl, description, isMonitoringEnabled)
    {
    }

    public MonitoringTransition RecordSuccessfulCheck(string name, DateTimeOffset checkedAtUtc)
    {
        return ApplySuccessfulCheck(name, checkedAtUtc);
    }
}
