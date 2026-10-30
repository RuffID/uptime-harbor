namespace UptimeHarbor.Application.Cluster.Models;

public record AdministrationConflictDto(Guid Id, Guid EntityId, string Kind, string Action, string? AccountAction,
    long BaseRevision, long CurrentRevision, AdministrationEntityDto? Baseline,
    AdministrationEntityDto? FirstAccepted, AdministrationSettingsDto? Proposed,
    AdministrationEntityDto? Current, Guid? ResolutionOperationId, DateTimeOffset? ResolvedAtUtc);
