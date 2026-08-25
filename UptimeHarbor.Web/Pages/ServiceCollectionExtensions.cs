namespace UptimeHarbor.Web.Pages;

using UptimeHarbor.Application.Security;
using UptimeHarbor.Web.Security;

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
        services.AddHealthChecks();
        services.AddHttpContextAccessor();
        services.AddScoped<IUserAccessContext, CurrentUserAccessContext>();

        return services;
    }
}
