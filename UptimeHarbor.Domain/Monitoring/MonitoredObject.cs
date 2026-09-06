namespace UptimeHarbor.Domain.Monitoring;

public abstract class MonitoredObject
{
    protected MonitoredObject()
    {
    }

    protected MonitoredObject(
        Guid id,
        string endpointUrl,
        string? description,
        bool isMonitoringEnabled)
    {
        EnsureValidId(id);
        string validatedEndpointUrl = ValidateEndpointUrl(endpointUrl);
        string? validatedDescription = ValidateDescription(description);

        Id = id;
        EndpointUrl = validatedEndpointUrl;
        Description = validatedDescription;
        IsMonitoringEnabled = isMonitoringEnabled;
        Status = MonitoringStatus.Unknown;
    }

    public Guid Id { get; private set; }

    public long Version { get; private set; }

    public string EndpointUrl { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public bool IsMonitoringEnabled { get; private set; }

    public string? LastKnownName { get; private set; }

    public MonitoringStatus Status { get; private set; }

    public int ConsecutiveFailures { get; private set; }

    public DateTimeOffset? LastCheckedAtUtc { get; private set; }

    public DateTimeOffset? LastSuccessfulCheckAtUtc { get; private set; }

    public Guid? CurrentIncidentId { get; private set; }

    public MonitoringTransition UpdateSettings(
        string endpointUrl,
        string? description,
        bool isMonitoringEnabled)
    {
        string validatedEndpointUrl = ValidateEndpointUrl(endpointUrl);
        string? validatedDescription = ValidateDescription(description);
        bool endpointChanged = !string.Equals(EndpointUrl, validatedEndpointUrl, StringComparison.Ordinal);
        bool monitoringDisabled = IsMonitoringEnabled && !isMonitoringEnabled;

        EndpointUrl = validatedEndpointUrl;
        Description = validatedDescription;
        IsMonitoringEnabled = isMonitoringEnabled;

        if (endpointChanged)
        {
            LastKnownName = null;
            LastCheckedAtUtc = null;
            LastSuccessfulCheckAtUtc = null;
            ResetMonitoringState();
            ResetEndpointSpecificState();
        }
        else if (monitoringDisabled || !isMonitoringEnabled && Status != MonitoringStatus.Unknown)
        {
            ResetMonitoringState();
        }
        else if (!IsMonitoringEnabled)
        {
            ResetMonitoringState();
        }

        IncrementVersion();

        return monitoringDisabled ? MonitoringTransition.MonitoringDisabled : MonitoringTransition.None;
    }

    public MonitoringTransition SetMonitoringEnabled(bool isMonitoringEnabled)
    {
        if (IsMonitoringEnabled == isMonitoringEnabled)
        {
            return MonitoringTransition.None;
        }

        IsMonitoringEnabled = isMonitoringEnabled;
        ResetMonitoringState();
        IncrementVersion();

        return isMonitoringEnabled
            ? MonitoringTransition.None
            : MonitoringTransition.MonitoringDisabled;
    }

    public MonitoringTransition RecordFailedCheck(
        DateTimeOffset checkedAtUtc,
        int failuresBeforeUnavailable)
    {
        EnsureMonitoringEnabled();
        EnsureUtc(checkedAtUtc, nameof(checkedAtUtc));

        if (failuresBeforeUnavailable <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failuresBeforeUnavailable),
                "Порог ошибок должен быть больше нуля.");
        }

        LastCheckedAtUtc = checkedAtUtc;
        ConsecutiveFailures++;
        IncrementVersion();

        if (ConsecutiveFailures < failuresBeforeUnavailable || Status == MonitoringStatus.Unavailable)
        {
            return MonitoringTransition.None;
        }

        Status = MonitoringStatus.Unavailable;
        CurrentIncidentId = Guid.NewGuid();
        return MonitoringTransition.BecameUnavailable;
    }

    protected MonitoringTransition ApplySuccessfulCheck(
        string name,
        DateTimeOffset checkedAtUtc)
    {
        EnsureMonitoringEnabled();
        EnsureUtc(checkedAtUtc, nameof(checkedAtUtc));
        string validatedName = ValidateObjectName(name);
        MonitoringStatus previousStatus = Status;

        LastKnownName = validatedName;
        LastCheckedAtUtc = checkedAtUtc;
        LastSuccessfulCheckAtUtc = checkedAtUtc;
        ConsecutiveFailures = 0;
        Status = MonitoringStatus.Available;
        IncrementVersion();

        return previousStatus switch
        {
            MonitoringStatus.Unknown => MonitoringTransition.BecameAvailable,
            MonitoringStatus.Unavailable => MonitoringTransition.Recovered,
            _ => MonitoringTransition.None
        };
    }

    protected static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Дата и время должны быть указаны в UTC.", parameterName);
        }
    }

    protected virtual void ResetEndpointSpecificState()
    {
    }

    private static void EnsureValidId(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Идентификатор не может быть пустым.", nameof(id));
        }
    }

    private static string ValidateEndpointUrl(string endpointUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointUrl);
        string value = endpointUrl.Trim();

        if (value.Length > ModelConstraints.ENDPOINT_URL_MAX_LENGTH)
        {
            throw new ArgumentException("URL endpoint превышает допустимую длину.", nameof(endpointUrl));
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArgumentException("URL endpoint должен использовать HTTP или HTTPS.", nameof(endpointUrl));
        }

        return value;
    }

    private static string? ValidateDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        string value = description.Trim();

        if (value.Length > ModelConstraints.DESCRIPTION_MAX_LENGTH)
        {
            throw new ArgumentException("Описание превышает допустимую длину.", nameof(description));
        }

        return value;
    }

    protected static string ValidateObjectName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string value = name.Trim();

        if (value.Length > ModelConstraints.OBJECT_NAME_MAX_LENGTH)
        {
            throw new ArgumentException("Имя объекта превышает допустимую длину.", nameof(name));
        }

        return value;
    }

    private void EnsureMonitoringEnabled()
    {
        if (!IsMonitoringEnabled)
        {
            throw new InvalidOperationException("Нельзя записать результат проверки отключённого объекта.");
        }
    }

    private void ResetMonitoringState()
    {
        Status = MonitoringStatus.Unknown;
        ConsecutiveFailures = 0;
        CurrentIncidentId = null;
    }

    private void IncrementVersion()
    {
        Version = checked(Version + 1);
    }
}
