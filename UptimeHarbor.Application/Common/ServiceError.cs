namespace UptimeHarbor.Application.Common;

public class ServiceError
{
    public ServiceError(ServiceErrorType type, string message, string? field = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Type = type;
        Message = message;
        Field = field;
    }

    public ServiceErrorType Type { get; }

    public string Message { get; }

    public string? Field { get; }
}
