using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;

namespace UptimeHarbor.Infrastructure.Persistence.UnitOfWork;

public class NotificationSettingsUnitOfWork(
    IUptimeHarborDbContextSession session,
    IMonitoredServerRepository monitoredServers,
    IMonitoredContainerRepository monitoredContainers,
    ITelegramChatRepository telegramChats,
    INotificationBindingRepository notificationBindings)
    : UptimeHarborDbUnitOfWork(session), INotificationSettingsUnitOfWork
{
    public IMonitoredServerRepository MonitoredServers { get; } = monitoredServers;

    public IMonitoredContainerRepository MonitoredContainers { get; } = monitoredContainers;

    public ITelegramChatRepository TelegramChats { get; } = telegramChats;

    public INotificationBindingRepository NotificationBindings { get; } = notificationBindings;
}
