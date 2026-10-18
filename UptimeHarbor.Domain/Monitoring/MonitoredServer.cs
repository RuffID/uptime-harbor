namespace UptimeHarbor.Domain.Monitoring;

public class MonitoredServer : MonitoredObject
{
    private MonitoredServer()
    {
    }

    public MonitoredServer(
        Guid id,
        string name,
        string endpointUrl,
        string? description,
        bool isMonitoringEnabled)
        : base(id, endpointUrl, description, isMonitoringEnabled)
    {
        string validatedName = ValidateObjectName(name);
        Name = validatedName;
        NormalizedName = NormalizeName(validatedName);
    }

    public string Name { get; private set; } = string.Empty;

    private string NormalizedName { get; set; } = string.Empty;

    public MonitoringTransition UpdateSettings(
        string name,
        string endpointUrl,
        string? description,
        bool isMonitoringEnabled)
    {
        string validatedName = ValidateObjectName(name);
        MonitoringTransition transition = base.UpdateSettings(
            endpointUrl,
            description,
            isMonitoringEnabled);
        Name = validatedName;
        NormalizedName = NormalizeName(validatedName);
        return transition;
    }

    public MonitoringTransition RecordSuccessfulCheck(string name, DateTimeOffset checkedAtUtc)
    {
        return ApplySuccessfulCheck(name, checkedAtUtc);
    }

    public void RecordContainerLinksChanged()
    {
        IncrementVersion();
    }

    private static string NormalizeName(string name)
    {
        return name.ToUpperInvariant();
    }
}
