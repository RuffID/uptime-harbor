using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Infrastructure;
using UptimeHarbor.Infrastructure.Persistence;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddInfrastructure_WhenConnectionStringIsMissing_ThrowsClearError()
    {
        ServiceCollection services = new();
        IConfiguration configuration = new ConfigurationBuilder().Build();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddInfrastructure(configuration));

        Assert.Equal("ConnectionStrings:PostgreSql is required.", exception.Message);
    }

    [Fact]
    public void AddInfrastructure_RegistersDataAccessAndHttpClientServices()
    {
        ServiceCollection services = new();
        services.AddLogging();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:PostgreSql"] =
                    "Host=localhost;Database=uptime_harbor_di_tests;Username=test;Password=test"
            })
            .Build();
        services.AddInfrastructure(configuration);

        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
        using IServiceScope scope = provider.CreateScope();

        IMonitoringUnitOfWork monitoring =
            scope.ServiceProvider.GetRequiredService<IMonitoringUnitOfWork>();
        IVisibilitySettingsUnitOfWork visibility =
            scope.ServiceProvider.GetRequiredService<IVisibilitySettingsUnitOfWork>();
        INotificationSettingsUnitOfWork notifications =
            scope.ServiceProvider.GetRequiredService<INotificationSettingsUnitOfWork>();

        Assert.NotNull(monitoring.MonitoredServers);
        Assert.NotNull(monitoring.MonitoredContainers);
        Assert.NotNull(visibility.VisibilityPermissions);
        Assert.NotNull(notifications.TelegramChats);
        Assert.NotNull(notifications.NotificationBindings);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType.FullName?.StartsWith(
                "EFCoreLibrary.Abstractions.Database.IAppDbContext`1",
                StringComparison.Ordinal) == true);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType.FullName ==
                "HttpClientLibrary.Abstractions.IHttpApiClient");
    }
}
