using UptimeHarbor.Application.Abstractions.Persistence;

namespace UptimeHarbor.Application.Abstractions.UnitOfWork;

public interface IVisibilitySettingsUnitOfWork : IUnitOfWorkScope
{
    IMonitoredServerRepository MonitoredServers { get; }

    IMonitoredContainerRepository MonitoredContainers { get; }

    IVisibilityPermissionRepository VisibilityPermissions { get; }
}
