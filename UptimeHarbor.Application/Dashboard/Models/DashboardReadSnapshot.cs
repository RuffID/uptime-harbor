namespace UptimeHarbor.Application.Dashboard.Models;

public record DashboardReadSnapshot(
    IReadOnlyList<DashboardServerReadRecord> Servers,
    IReadOnlyList<DashboardContainerReadRecord> Containers,
    IReadOnlyList<Guid> PermittedServerIds,
    IReadOnlyList<Guid> PermittedContainerIds);
