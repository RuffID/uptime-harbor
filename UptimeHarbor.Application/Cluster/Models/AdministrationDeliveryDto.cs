namespace UptimeHarbor.Application.Cluster.Models;

public record AdministrationDeliveryDto(Guid Id, Guid ChatId, Guid? IncidentId, string Kind, string Status,
    int Attempts, string? Failure, DateTimeOffset? CompletedAtUtc);
