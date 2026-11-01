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

    public DateTimeOffset? StartedAtUtc { get; private set; }

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

    public MonitoringTransition RecordSuccessfulCheck(
        string name,
        DateTimeOffset startedAtUtc,
        DateTimeOffset checkedAtUtc)
    {
        EnsureUtc(startedAtUtc, nameof(startedAtUtc));

        if (startedAtUtc > checkedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startedAtUtc),
                "Время запуска сервера не может находиться в будущем.");
        }

        MonitoringTransition transition = ApplySuccessfulCheck(name, checkedAtUtc);
        StartedAtUtc = startedAtUtc;
        return transition;
    }

    protected override void ResetEndpointSpecificState()
    {
        StartedAtUtc = null;
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
