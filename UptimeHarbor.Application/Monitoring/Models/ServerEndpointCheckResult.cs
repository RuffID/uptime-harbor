namespace UptimeHarbor.Application.Monitoring.Models;

public class ServerEndpointCheckResult
{
    private ServerEndpointCheckResult(
        bool success,
        string? name,
        DateTimeOffset? startedAtUtc,
        EndpointCheckFailureKind? failureKind,
        DateTimeOffset checkedAtUtc)
    {
        Success = success;
        Name = name;
        StartedAtUtc = startedAtUtc;
        FailureKind = failureKind;
        CheckedAtUtc = checkedAtUtc;
    }

    public bool Success { get; }

    public string? Name { get; }

    public DateTimeOffset? StartedAtUtc { get; }

    public EndpointCheckFailureKind? FailureKind { get; }

    public DateTimeOffset CheckedAtUtc { get; }

    public static ServerEndpointCheckResult Succeeded(
        string name,
        DateTimeOffset startedAtUtc,
        DateTimeOffset checkedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnsureUtc(startedAtUtc, nameof(startedAtUtc));
        EnsureUtc(checkedAtUtc, nameof(checkedAtUtc));
        if (startedAtUtc > checkedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(startedAtUtc));
        }

        return new ServerEndpointCheckResult(true, name, startedAtUtc, null, checkedAtUtc);
    }

    public static ServerEndpointCheckResult Failed(
        EndpointCheckFailureKind failureKind,
        DateTimeOffset checkedAtUtc)
    {
        EnsureUtc(checkedAtUtc, nameof(checkedAtUtc));
        return new ServerEndpointCheckResult(false, null, null, failureKind, checkedAtUtc);
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Дата и время должны быть указаны в UTC.", parameterName);
        }
    }
}
