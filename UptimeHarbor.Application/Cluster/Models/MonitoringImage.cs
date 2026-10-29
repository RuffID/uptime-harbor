using System.Collections.Immutable;

namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Часть accepted image: экспортируется тем же snapshot и воспроизводится тем же WAL.</summary>
public record MonitoringImage
{
    public ImmutableDictionary<Guid, Guid> Nodes { get; init; } = ImmutableDictionary<Guid, Guid>.Empty;
    public ImmutableDictionary<Guid, MonitoringObservation> Observations { get; init; } = ImmutableDictionary<Guid, MonitoringObservation>.Empty;
    public ImmutableDictionary<Guid, ObservationAcceptance> Acceptances { get; init; } = ImmutableDictionary<Guid, ObservationAcceptance>.Empty;
    public ImmutableDictionary<Guid, MonitoringStream> Streams { get; init; } = ImmutableDictionary<Guid, MonitoringStream>.Empty;
    public ImmutableDictionary<Guid, MonitoringEvaluation> Live { get; init; } = ImmutableDictionary<Guid, MonitoringEvaluation>.Empty;
    public ImmutableDictionary<Guid, MonitoringEvaluation> History { get; init; } = ImmutableDictionary<Guid, MonitoringEvaluation>.Empty;
    public ImmutableDictionary<Guid, MonitoringIncident> Incidents { get; init; } = ImmutableDictionary<Guid, MonitoringIncident>.Empty;
    public ImmutableDictionary<Guid, MonitoringEvent> Events { get; init; } = ImmutableDictionary<Guid, MonitoringEvent>.Empty;
    public ImmutableDictionary<Guid, MonitoringIntent> Intents { get; init; } = ImmutableDictionary<Guid, MonitoringIntent>.Empty;
    public ImmutableDictionary<Guid, MonitoringSummary> Summaries { get; init; } = ImmutableDictionary<Guid, MonitoringSummary>.Empty;
    public ImmutableDictionary<Guid, MonitoringDeliveryEvidence> ConfirmedDeliveries { get; init; } = ImmutableDictionary<Guid, MonitoringDeliveryEvidence>.Empty;
    public ImmutableDictionary<Guid, AcceptedDeliveryAttempt> Attempts { get; init; } = ImmutableDictionary<Guid, AcceptedDeliveryAttempt>.Empty;
}
