using Microsoft.Extensions.DependencyInjection;
using UptimeHarbor.Infrastructure.Identity;

namespace UptimeHarbor.Infrastructure;

public static class InfrastructureInitializationExtensions
{
    public static async Task InitializeInfrastructureAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        InitialAdministratorBootstrapper bootstrapper =
            scope.ServiceProvider.GetRequiredService<InitialAdministratorBootstrapper>();
        await bootstrapper.InitializeAsync(cancellationToken);
    }
}
