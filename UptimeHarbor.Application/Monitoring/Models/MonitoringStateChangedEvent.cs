using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Application.Monitoring.Models;

public record MonitoringStateChangedEvent(
    Guid ObjectId,
    MonitoringObjectKind ObjectKind,
    Guid? ServerId,
    string? Name,
    string? Description,
    MonitoringStatus Status,
    Guid? CurrentIncidentId,
    int ConsecutiveFailures,
    DateTimeOffset CheckedAtUtc,
    DateTimeOffset? StartedAtUtc,
    MonitoringTransition Transition,
    bool CheckSucceeded,
    EndpointCheckFailureKind? FailureKind);
