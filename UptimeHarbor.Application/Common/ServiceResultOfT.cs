namespace UptimeHarbor.Application.Common;

public class ServiceResult<T>
{
    private ServiceResult(bool success, T? data, ServiceError? error)
    {
        Success = success;
        Data = data;
        Error = error;
    }

    public bool Success { get; }

    public T? Data { get; }

    public ServiceError? Error { get; }

    public static ServiceResult<T> Ok(T data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new ServiceResult<T>(true, data, null);
    }

    public static ServiceResult<T> Fail(ServiceError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new ServiceResult<T>(false, default, error);
    }

    public static ServiceResult<T> Fail(ServiceErrorType type, string message) =>
        Fail(new ServiceError(type, message));
}
