namespace UptimeHarbor.Application.Monitoring.Models;

public class ServerEndpointCheckResult
{
    private ServerEndpointCheckResult(
        bool success,
        string? name,
        EndpointCheckFailureKind? failureKind,
        DateTimeOffset checkedAtUtc)
    {
        Success = success;
        Name = name;
        FailureKind = failureKind;
        CheckedAtUtc = checkedAtUtc;
    }

    public bool Success { get; }

    public string? Name { get; }

    public EndpointCheckFailureKind? FailureKind { get; }

    public DateTimeOffset CheckedAtUtc { get; }

    public static ServerEndpointCheckResult Succeeded(
        string name,
        DateTimeOffset checkedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnsureUtc(checkedAtUtc);
        return new ServerEndpointCheckResult(true, name, null, checkedAtUtc);
    }

    public static ServerEndpointCheckResult Failed(
        EndpointCheckFailureKind failureKind,
        DateTimeOffset checkedAtUtc)
    {
        EnsureUtc(checkedAtUtc);
        return new ServerEndpointCheckResult(false, null, failureKind, checkedAtUtc);
    }

    private static void EnsureUtc(DateTimeOffset checkedAtUtc)
    {
        if (checkedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Время проверки должно быть указано в UTC.", nameof(checkedAtUtc));
        }
    }
}
