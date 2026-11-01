namespace UptimeHarbor.Application.Cluster.Models;

public record AdministrationOperationDto(Guid OperationId, string Status, long? AppliedRevision,
    Guid? ConflictId, Guid? ReplacedBy, string? Message)
{
    public AdministrationOperationDto? ReplacementResult { get; init; }
}
