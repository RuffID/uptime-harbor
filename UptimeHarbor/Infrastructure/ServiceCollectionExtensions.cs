using EFCoreLibrary.Abstractions.Database;
using EFCoreLibrary.EfCore;
using EFCoreLibrary.Extensions;
using HttpClientLibrary.Abstractions;
using HttpClientLibrary.Clients;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Infrastructure.Persistence;
using UptimeHarbor.Infrastructure.Persistence.Repositories;
using UptimeHarbor.Infrastructure.Persistence.UnitOfWork;

namespace UptimeHarbor.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        string? connectionString = configuration.GetConnectionString("PostgreSql");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:PostgreSql is required.");
        }

        services.AddDbContext<UptimeHarborDbContext>(options =>
            options.UseNpgsql(connectionString));
        services.AddScoped<IAppDbContext<UptimeHarborDbContext>, EfDbContextAdapter<UptimeHarborDbContext>>();
        services.AddEfCoreBaseRepositories<UptimeHarborDbContext>();

        services.AddScoped<IMonitoredServerRepository, MonitoredServerRepository>();
        services.AddScoped<IMonitoredContainerRepository, MonitoredContainerRepository>();
        services.AddScoped<ITelegramChatRepository, TelegramChatRepository>();
        services.AddScoped<INotificationBindingRepository, NotificationBindingRepository>();
        services.AddScoped<IVisibilityPermissionRepository, VisibilityPermissionRepository>();
        services.AddScoped<IUptimeHarborDbContextSession, UptimeHarborDbContextSession>();
        services.AddScoped<IMonitoringUnitOfWork, MonitoringUnitOfWork>();
        services.AddScoped<IVisibilitySettingsUnitOfWork, VisibilitySettingsUnitOfWork>();
        services.AddScoped<INotificationSettingsUnitOfWork, NotificationSettingsUnitOfWork>();

        services
            .AddDataProtection()
            .PersistKeysToDbContext<UptimeHarborDbContext>();

        services.AddHttpClient<IHttpApiClient, HttpApiClient>();

        return services;
    }
}
