namespace UptimeHarbor.Application.Monitoring.Models;

public class ContainerEndpointCheckResult
{
    private ContainerEndpointCheckResult(
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

    public static ContainerEndpointCheckResult Succeeded(
        string name,
        DateTimeOffset startedAtUtc,
        DateTimeOffset checkedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnsureUtc(startedAtUtc, nameof(startedAtUtc));
        EnsureUtc(checkedAtUtc, nameof(checkedAtUtc));
        return new ContainerEndpointCheckResult(true, name, startedAtUtc, null, checkedAtUtc);
    }

    public static ContainerEndpointCheckResult Failed(
        EndpointCheckFailureKind failureKind,
        DateTimeOffset checkedAtUtc)
    {
        EnsureUtc(checkedAtUtc, nameof(checkedAtUtc));
        return new ContainerEndpointCheckResult(false, null, null, failureKind, checkedAtUtc);
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Дата и время должны быть указаны в UTC.", parameterName);
        }
    }
}
