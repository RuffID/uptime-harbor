using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;

namespace UptimeHarbor.Infrastructure.Persistence.UnitOfWork;

public class VisibilitySettingsUnitOfWork(
    IUptimeHarborDbContextSession session,
    IMonitoredServerRepository monitoredServers,
    IMonitoredContainerRepository monitoredContainers,
    IVisibilityPermissionRepository visibilityPermissions)
    : UptimeHarborDbUnitOfWork(session), IVisibilitySettingsUnitOfWork
{
    public IMonitoredServerRepository MonitoredServers { get; } = monitoredServers;

    public IMonitoredContainerRepository MonitoredContainers { get; } = monitoredContainers;

    public IVisibilityPermissionRepository VisibilityPermissions { get; } = visibilityPermissions;
}
