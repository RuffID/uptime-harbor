using Microsoft.Extensions.DependencyInjection;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Infrastructure.Persistence;
using UptimeHarbor.Infrastructure.Services.DataBase;

namespace UptimeHarbor.Infrastructure;

public static class InfrastructureInitializationExtensions
{
    private const int DATABASE_STARTUP_TIMEOUT_SECONDS = 60;

    public static async Task InitializeInfrastructureAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        await using AsyncServiceScope scope = services.CreateAsyncScope();

        DataBaseCheckUpService<UptimeHarborDbContext> databaseCheckUp =
            scope.ServiceProvider
                .GetRequiredService<DataBaseCheckUpService<UptimeHarborDbContext>>();
        using CancellationTokenSource timeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(
            TimeSpan.FromSeconds(DATABASE_STARTUP_TIMEOUT_SECONDS));
        try
        {
            await databaseCheckUp.CheckOrUpdateDatabaseAsync(timeoutCancellation.Token);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Database initialization did not finish within " +
                $"{DATABASE_STARTUP_TIMEOUT_SECONDS} seconds.",
                exception);
        }

        InitialAdministratorBootstrapper bootstrapper =
            scope.ServiceProvider.GetRequiredService<InitialAdministratorBootstrapper>();
        await bootstrapper.InitializeAsync(cancellationToken);
    }
}
