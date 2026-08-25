namespace UptimeHarbor.Pages;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWebPresentation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddRazorPages();
        services.AddSignalR();
        services.AddHealthChecks();

        return services;
    }
}
