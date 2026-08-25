using UptimeHarbor.Application.Abstractions.Persistence;

namespace UptimeHarbor.Application.Abstractions.UnitOfWork;

public interface INotificationSettingsUnitOfWork : IUnitOfWorkScope
{
    IMonitoredServerRepository MonitoredServers { get; }

    IMonitoredContainerRepository MonitoredContainers { get; }

    ITelegramChatRepository TelegramChats { get; }

    INotificationBindingRepository NotificationBindings { get; }
}
