namespace UptimeHarbor.Application.Monitoring;

public enum EndpointCheckFailureKind
{
    Timeout = 0,
    Network = 1,
    HttpStatus = 2,
    InvalidJson = 3,
    InvalidName = 4,
    InvalidStartedAtUtc = 5
}
