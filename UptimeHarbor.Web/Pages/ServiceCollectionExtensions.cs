namespace UptimeHarbor.Web.Pages;

using UptimeHarbor.Application.Security;
using UptimeHarbor.Application.ObjectAdministration;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Web.Hubs;
using UptimeHarbor.Web.Security;
using UptimeHarbor.Web.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWebPresentation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddControllers();
        services.AddRazorPages(options =>
        {
            options.Conventions.AuthorizeFolder("/");
            options.Conventions.AllowAnonymousToPage("/Account/Login");
            options.Conventions.AllowAnonymousToPage("/Error");
        });
        services.AddSignalR();
        services.AddHealthChecks()
            .AddCheck("web", () => HealthCheckResult.Healthy(), tags: ["live"])
            .AddCheck<PostgreSqlHealthCheck>("postgresql", tags: ["ready"]);
        services.AddHttpContextAccessor();
        services.AddScoped<IUserAccessContext, CurrentUserAccessContext>();
        services.AddScoped<IObjectAdministrationChangePublisher, SignalRObjectAdministrationChangePublisher>();
        services.AddScoped<IMonitoringEventHandler, SignalRMonitoringEventHandler>();

        return services;
    }
}
