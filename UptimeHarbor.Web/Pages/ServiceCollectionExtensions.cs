namespace UptimeHarbor.Web.Pages;

using UptimeHarbor.Application.Security;
using UptimeHarbor.Application.ObjectAdministration;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Web.Hubs;
using UptimeHarbor.Web.Security;
using UptimeHarbor.Web.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using UptimeHarbor.Web.Http;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWebPresentation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services
            .AddControllers()
            .ConfigureApiBehaviorOptions(options =>
                options.InvalidModelStateResponseFactory =
                    ModelStateErrorResponseFactory.Create);
        services.AddRazorPages(options =>
        {
            options.Conventions.AuthorizeFolder("/");
            options.Conventions.AllowAnonymousToPage("/Account/Login");
            options.Conventions.AllowAnonymousToPage("/Error");
        });
        services.AddSignalR();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
                await context.HttpContext.Response.WriteAsync(
                    "Слишком много попыток входа. Повторите позже.",
                    cancellationToken);
            };
            options.AddPolicy(
                WebSecurityPolicies.LOGIN_RATE_LIMIT_POLICY,
                context => RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true
                    }));
        });
        services.AddHealthChecks()
            .AddCheck("web", () => HealthCheckResult.Healthy(), tags: ["live"])
            .AddCheck<SqlServerHealthCheck>("sqlserver", tags: ["ready"]);
        services.AddHttpContextAccessor();
        services.AddScoped<IUserAccessContext, CurrentUserAccessContext>();
        services.AddScoped<IObjectAdministrationChangePublisher, SignalRObjectAdministrationChangePublisher>();
        services.AddScoped<IMonitoringEventHandler, SignalRMonitoringEventHandler>();

        return services;
    }
}
