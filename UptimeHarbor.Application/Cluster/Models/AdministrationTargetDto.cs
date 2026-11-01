namespace UptimeHarbor.Application.Cluster.Models;

public record AdministrationTargetDto(Guid Id, string Availability, string Coverage, DateTimeOffset? EvaluatedAtUtc,
    bool EvaluationStale, IReadOnlyList<AdministrationObserverDto> Observers);
