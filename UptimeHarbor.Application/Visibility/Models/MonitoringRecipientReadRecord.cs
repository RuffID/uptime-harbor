namespace UptimeHarbor.Application.Visibility.Models;

public record MonitoringRecipientReadRecord(
    Guid UserId,
    bool IsEnabled,
    bool IsAdministrator,
    IReadOnlyList<Guid> PermittedServerIds,
    IReadOnlyList<Guid> PermittedContainerIds);
