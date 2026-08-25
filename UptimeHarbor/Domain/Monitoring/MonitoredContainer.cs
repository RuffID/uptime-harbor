namespace UptimeHarbor.Domain.Monitoring;

public class MonitoredContainer : MonitoredObject
{
    private MonitoredContainer()
    {
    }

    public MonitoredContainer(
        Guid id,
        Guid serverId,
        string endpointUrl,
        string? description,
        bool isMonitoringEnabled)
        : base(id, endpointUrl, description, isMonitoringEnabled)
    {
        if (serverId == Guid.Empty)
        {
            throw new ArgumentException("Идентификатор сервера не может быть пустым.", nameof(serverId));
        }

        ServerId = serverId;
    }

    public Guid ServerId { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

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
                "Время запуска контейнера не может находиться в будущем.");
        }

        MonitoringTransition transition = ApplySuccessfulCheck(name, checkedAtUtc);
        StartedAtUtc = startedAtUtc;
        return transition;
    }

    protected override void ResetEndpointSpecificState()
    {
        StartedAtUtc = null;
    }
}
