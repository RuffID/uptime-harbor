namespace UptimeHarbor.Application.Users.Models;

/// <summary>Принятие HTTP-заявки не означает применения изменения аккаунта.</summary>
public record AccountOperationDto(Guid OperationId, Guid UserId, string Status,
    long? Revision, Guid? ConflictId)
{
    public UptimeHarbor.Application.Cluster.Models.AdministrationOperationDto? Outcome { get; init; }
}
