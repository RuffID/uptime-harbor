namespace UptimeHarbor.Web.Configuration;

public class MonitoringOptions
{
    public const string SECTION_NAME = "Monitoring";

    public int IntervalSeconds { get; init; }

    public int TimeoutSeconds { get; init; }

    public int FailuresBeforeUnavailable { get; init; }
}
