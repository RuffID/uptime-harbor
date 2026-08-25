namespace UptimeHarbor.Application.Common;

public class ServiceResult
{
    private ServiceResult(bool success, ServiceError? error)
    {
        Success = success;
        Error = error;
    }

    public bool Success { get; }

    public ServiceError? Error { get; }

    public static ServiceResult Ok() => new(true, null);

    public static ServiceResult Fail(ServiceError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new ServiceResult(false, error);
    }

    public static ServiceResult Fail(ServiceErrorType type, string message) =>
        Fail(new ServiceError(type, message));

    public static ServiceResult Fail(ServiceErrorType type, string message, string field) =>
        Fail(new ServiceError(type, message, field));
}
