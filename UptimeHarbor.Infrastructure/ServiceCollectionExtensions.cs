using EFCoreLibrary.Abstractions.Database;
using EFCoreLibrary.EfCore;
using EFCoreLibrary.Extensions;
using HttpClientLibrary.Abstractions;
using HttpClientLibrary.Clients;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Infrastructure.Persistence;
using UptimeHarbor.Infrastructure.Persistence.Repositories;
using UptimeHarbor.Infrastructure.Persistence.UnitOfWork;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Application.Users;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Infrastructure.Monitoring;
using UptimeHarbor.Application.Notifications;
using UptimeHarbor.Infrastructure.Telegram;

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

        services
            .AddOptions<BootstrapAdminOptions>()
            .Bind(configuration.GetSection(BootstrapAdminOptions.SECTION_NAME))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Login),
                $"{BootstrapAdminOptions.SECTION_NAME}:Login is required.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Password),
                $"{BootstrapAdminOptions.SECTION_NAME}:Password is required.");

        services
            .AddIdentity<ApplicationUser, ApplicationRole>(options =>
            {
                options.User.RequireUniqueEmail = false;
                options.SignIn.RequireConfirmedAccount = false;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddEntityFrameworkStores<UptimeHarborDbContext>()
            .AddDefaultTokenProviders();
        services.Configure<SecurityStampValidatorOptions>(options =>
            options.ValidationInterval = TimeSpan.Zero);
        services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/Account/Login";
            options.AccessDeniedPath = "/Account/AccessDenied";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
            options.Events.OnValidatePrincipal = ValidateEnabledUserAsync;
        });
        services.AddScoped<IAppDbContext<UptimeHarborDbContext>, EfDbContextAdapter<UptimeHarborDbContext>>();
        services.AddEfCoreBaseRepositories<UptimeHarborDbContext>();

        services.AddScoped<IMonitoredServerRepository, MonitoredServerRepository>();
        services.AddScoped<IMonitoredContainerRepository, MonitoredContainerRepository>();
        services.AddScoped<ITelegramChatRepository, TelegramChatRepository>();
        services.AddScoped<INotificationBindingRepository, NotificationBindingRepository>();
        services.AddScoped<IVisibilityPermissionRepository, VisibilityPermissionRepository>();
        services.AddScoped<IUptimeHarborDbContextSession, UptimeHarborDbContextSession>();
        services.AddScoped<IMonitoringUnitOfWork, MonitoringUnitOfWork>();
        services.AddScoped<IObjectAdministrationUnitOfWork, ObjectAdministrationUnitOfWork>();
        services.AddScoped<IVisibilitySettingsUnitOfWork, VisibilitySettingsUnitOfWork>();
        services.AddScoped<INotificationSettingsUnitOfWork, NotificationSettingsUnitOfWork>();
        services.AddScoped<IdentityUserAccountStore>();
        services.AddScoped<IUserAccountStore>(provider =>
            provider.GetRequiredService<IdentityUserAccountStore>());
        services.AddScoped<IInitialAdministratorStore>(provider =>
            provider.GetRequiredService<IdentityUserAccountStore>());
        services.AddScoped<InitialAdministratorBootstrapper>();
        services.AddScoped<IUserSignInService, UserSignInService>();

        services
            .AddDataProtection()
            .PersistKeysToDbContext<UptimeHarborDbContext>();

        services.AddHttpClient<IHttpApiClient, HttpApiClient>();
        services.AddHttpClient(HttpMonitoringEndpointChecker.HTTP_CLIENT_NAME, client =>
            client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(
                HttpMonitoringEndpointChecker.CreatePrimaryHandler);
        services.AddSingleton<HttpMonitoringEndpointChecker>();
        services.AddSingleton<IServerEndpointChecker>(provider =>
            provider.GetRequiredService<HttpMonitoringEndpointChecker>());
        services.AddSingleton<IContainerEndpointChecker>(provider =>
            provider.GetRequiredService<HttpMonitoringEndpointChecker>());
        services.AddSingleton<IMonitoringCycleRunner, MonitoringCycleRunner>();
        services.AddHostedService<MonitoringWorker>();

        services.AddHttpClient<TelegramHttpNotificationSender>(client =>
        {
            client.BaseAddress = new Uri("https://api.telegram.org/");
            client.Timeout = TimeSpan.FromSeconds(30);
        }).RemoveAllLoggers();
        services.AddScoped<INotificationSender>(provider =>
            provider.GetRequiredService<TelegramHttpNotificationSender>());
        services.AddScoped<NotificationOutboxStore>();
        services.AddSingleton<TelegramOutboxDispatcher>();
        services.AddHostedService<TelegramOutboxWorker>();

        return services;
    }

    private static async Task ValidateEnabledUserAsync(CookieValidatePrincipalContext context)
    {
        ISecurityStampValidator securityStampValidator = context.HttpContext.RequestServices
            .GetRequiredService<ISecurityStampValidator>();
        await securityStampValidator.ValidateAsync(context);
        if (context.Principal?.Identity?.IsAuthenticated != true)
        {
            return;
        }

        UserManager<ApplicationUser> userManager = context.HttpContext.RequestServices
            .GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser? user = await userManager.GetUserAsync(context.Principal);
        if (user is null || !user.IsEnabled)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        }
    }
}
