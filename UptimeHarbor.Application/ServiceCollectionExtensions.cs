using Microsoft.Extensions.DependencyInjection;
using UptimeHarbor.Application.Dashboard;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Notifications;
using UptimeHarbor.Application.ObjectAdministration;
using UptimeHarbor.Application.Users;
using UptimeHarbor.Application.Visibility;

namespace UptimeHarbor.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IUserManagementService, UserManagementService>();
        services.AddScoped<IDashboardQuery, DashboardQuery>();
        services.AddScoped<IMonitoringEventRecipientResolver, MonitoringEventRecipientResolver>();
        services.AddScoped<IObjectAdministrationService, ObjectAdministrationService>();
        services.AddScoped<IMonitoringService, MonitoringService>();
        services.AddScoped<IMonitoringEventPublisher, MonitoringEventPublisher>();
        services.AddScoped<INotificationAdministrationService, NotificationAdministrationService>();
        services.AddScoped<INotificationDeliveryService, NotificationDeliveryService>();

        return services;
    }
}
