namespace UptimeHarbor.Domain.Monitoring;

public enum MonitoringTransition
{
    None = 0,
    BecameAvailable = 1,
    BecameUnavailable = 2,
    Recovered = 3,
    MonitoringDisabled = 4
}
