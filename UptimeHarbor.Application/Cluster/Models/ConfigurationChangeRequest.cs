namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Пользователь выбирает значение и базу; actor, origin, generation и provenance назначает сервер.</summary>
public record ConfigurationChangeRequest(Guid OperationId, Guid EntityId, string Kind, long BaseRevision,
    string Action, AdministrationSettingsDto? Settings = null, Guid? ResolvesConflictId = null,
    Guid? TargetOperationId = null, Guid? ControlOperationId = null, bool ConfirmRestored = false,
    bool ConfirmGlobalDelete = false);
