namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Устойчивый результат одного stream. Получение пакета не меняет время проверки.</summary>
public record MonitoringObservation(Guid Id, Guid TargetId, Guid ObserverNodeId, Guid StorageGeneration,
    Guid CheckGeneration, long ConfigRevision, long Sequence, DateTimeOffset CheckedAtUtc,
    long DurationMilliseconds, bool Succeeded, string? FailureKind, string? EndpointName,
    DateTimeOffset? StartedAtUtc, bool ClockConfirmed)
{
    public override string ToString()
    {
        return $"Observation {Id}";
    }
}
