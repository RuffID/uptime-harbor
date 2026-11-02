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
        bool isMonitoringEnabled,
        ServerType type = ServerType.Server,
        Guid? serverId = null)
        : base(id, endpointUrl, description, isMonitoringEnabled)
    {
        string validatedName = ValidateObjectName(name);
        Name = validatedName;
        NormalizedName = NormalizeName(validatedName);
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        Type = type;
        ValidateParent(serverId);
        ServerId = serverId;
    }

    public static MonitoredServer CreateApplication(
        Guid id,
        Guid? serverId,
        string endpointUrl,
        string? description,
        bool isMonitoringEnabled,
        string? name = null)
    {
        return new MonitoredServer(id, name ?? $"Приложение {id:N}", endpointUrl, description,
            isMonitoringEnabled, ServerType.Application, serverId);
    }

    public ServerType Type { get; private set; }

    public Guid? ServerId { get; private set; }

    public void AssignServer(Guid? serverId)
    {
        ValidateParent(serverId);
        if (ServerId == serverId)
        {
            return;
        }

        ServerId = serverId;
        IncrementVersion();
    }

    private void ValidateParent(Guid? serverId)
    {
        if (serverId == Guid.Empty || serverId == Id)
        {
            throw new ArgumentException("Недопустимый идентификатор связанного сервера.", nameof(serverId));
        }

        if (serverId is not null && Type != ServerType.Application)
        {
            throw new ArgumentException("Связь с сервером доступна только приложению.", nameof(serverId));
        }
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
