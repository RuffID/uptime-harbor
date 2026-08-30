using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Builder;
using UptimeHarbor.Application;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Monitoring.Models;
using UptimeHarbor.Application.Notifications;
using UptimeHarbor.Application.Notifications.Models;
using UptimeHarbor.Infrastructure;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Infrastructure.Persistence;
using UptimeHarbor.Web.Configuration;
using UptimeHarbor.Web.Pages;
using Xunit;

namespace UptimeHarbor.Tests.Web.DependencyInjection;

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
        services.AddScoped<INotificationDeliveryService, StubNotificationDeliveryService>();
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
        IObjectAdministrationUnitOfWork objectAdministration =
            scope.ServiceProvider.GetRequiredService<IObjectAdministrationUnitOfWork>();
        IVisibilitySettingsUnitOfWork visibility =
            scope.ServiceProvider.GetRequiredService<IVisibilitySettingsUnitOfWork>();
        INotificationSettingsUnitOfWork notifications =
            scope.ServiceProvider.GetRequiredService<INotificationSettingsUnitOfWork>();

        Assert.NotNull(monitoring.MonitoredServers);
        Assert.NotNull(monitoring.MonitoredContainers);
        Assert.NotNull(objectAdministration.MonitoredServers);
        Assert.NotNull(objectAdministration.MonitoredContainers);
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

    [Fact]
    public void CompleteRegistration_PassesDiValidationAndConfiguresImmediateCookieInvalidation()
    {
        IConfiguration configuration = CreateCompleteConfiguration();
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });

        builder.Services
            .AddApplication()
            .AddApplicationConfiguration(configuration)
            .AddInfrastructure(configuration)
            .AddWebPresentation();

        using WebApplication application = builder.Build();

        SecurityStampValidatorOptions stampOptions = application.Services
            .GetRequiredService<IOptions<SecurityStampValidatorOptions>>()
            .Value;
        IdentityOptions identityOptions = application.Services
            .GetRequiredService<IOptions<IdentityOptions>>()
            .Value;
        Assert.Equal(TimeSpan.Zero, stampOptions.ValidationInterval);
        Assert.True(identityOptions.Lockout.AllowedForNewUsers);
        Assert.Equal(5, identityOptions.Lockout.MaxFailedAccessAttempts);
        Assert.Equal(TimeSpan.FromMinutes(5), identityOptions.Lockout.DefaultLockoutTimeSpan);
    }

    [Fact]
    public void BootstrapOptions_WhenAdministratorIsNeeded_AreValidated()
    {
        ServiceCollection services = new();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:PostgreSql"] =
                    "Host=localhost;Database=uptime_harbor_di_tests;Username=test;Password=test",
                ["BootstrapAdmin:Login"] = "",
                ["BootstrapAdmin:Password"] = ""
            })
            .Build();
        services.AddInfrastructure(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<BootstrapAdminOptions>>().Value);

        Assert.Contains("BootstrapAdmin:Login is required.", exception.Failures);
        Assert.Contains("BootstrapAdmin:Password is required.", exception.Failures);
    }

    private static IConfiguration CreateCompleteConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:PostgreSql"] =
                    "Host=localhost;Database=uptime_harbor_di_tests;Username=test;Password=test",
                ["BootstrapAdmin:Login"] = "admin",
                ["BootstrapAdmin:Password"] = "initial-password",
                ["Monitoring:IntervalSeconds"] = "30",
                ["Monitoring:TimeoutSeconds"] = "30",
                ["Monitoring:FailuresBeforeUnavailable"] = "3",
                ["Telegram:BotToken"] = "test-token"
            })
            .Build();
    }

    private class StubNotificationDeliveryService : INotificationDeliveryService
    {
        public Task<IReadOnlyList<PendingNotificationDeliveryDto>> GetPendingDeliveriesAsync(
            MonitoringStateChangedEvent monitoringEvent,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PendingNotificationDeliveryDto>>([]);

        public Task<ServiceResult> MarkDeliverySuccessfulAsync(
            Guid bindingId,
            Guid incidentId,
            NotificationDeliveryKind kind,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ServiceResult.Ok());
    }
}
