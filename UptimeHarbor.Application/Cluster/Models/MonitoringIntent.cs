using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Models;

public record MonitoringIntent(Guid Id, Guid? IncidentId, Guid? EventId, Guid ChatId, DeliveryKind Kind,
    Guid BindingGeneration, string LogicalKey, string Payload, DeliveryState State,
    Guid? SummaryId, DateTimeOffset? CompletedAtUtc, long Index, long Term)
{
    public Guid? ActorUserId { get; init; }
    public DateTimeOffset? NextAttemptAtUtc { get; init; }
}
