namespace UptimeHarbor.Application.Common;

public class ServiceError
{
    public ServiceError(ServiceErrorType type, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Type = type;
        Message = message;
    }

    public ServiceErrorType Type { get; }

    public string Message { get; }
}
