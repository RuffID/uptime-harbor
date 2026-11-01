namespace UptimeHarbor.Application.Cluster.Models;

public record AdministrationPendingDto(Guid OperationId, Guid EntityId, string Kind, string Action,
    long BaseRevision, string State, DateTimeOffset CreatedAtUtc, AdministrationSettingsDto? Settings,
    Guid? RequestedControlId, Guid? ReplacementOperationId, AdministrationOperationDto? Result,
    string? AccountAction);
