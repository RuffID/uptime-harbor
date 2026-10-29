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
using Microsoft.Extensions.Logging;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Infrastructure.Persistence;
using UptimeHarbor.Infrastructure.Persistence.Repositories;
using UptimeHarbor.Infrastructure.Persistence.Queries;
using UptimeHarbor.Infrastructure.Persistence.UnitOfWork;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Application.Users;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Infrastructure.Monitoring;
using UptimeHarbor.Application.Notifications;
using UptimeHarbor.Infrastructure.Telegram;
using UptimeHarbor.Application.Dashboard;
using UptimeHarbor.Application.Visibility;
using UptimeHarbor.Infrastructure.Services.DataBase;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Application.Security;

namespace UptimeHarbor.Infrastructure;

public static class ServiceCollectionExtensions
{
    private const string LINUX_SQL_SERVER_BACKUP_FOLDER = "/var/opt/mssql/backups";

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddClusterInfrastructure(configuration);
        ClusterOptions clusterOptions = configuration.GetSection(ClusterOptions.SECTION_NAME).Get<ClusterOptions>() ?? new();
        if (clusterOptions.Enabled)
        {
            services.AddScoped<IAdministratorAccessPolicy, AcceptedAdministratorAccessPolicy>();
        }

        string? connectionString = configuration.GetConnectionString("SqlServer");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:SqlServer is required.");
        }

        services.AddDbContext<UptimeHarborDbContext>(options =>
            options.UseSqlServer(connectionString));

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
        services.Configure<PasswordHasherOptions>(options =>
        {
            options.CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3;
            options.IterationCount = 100000;
        });
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
        services.AddEfCoreContext<UptimeHarborDbContext, UptimeHarborDbContext>();
        services.AddEfCoreBaseRepositories<UptimeHarborDbContext>();

        services.AddScoped<DataBaseCheckUpService<UptimeHarborDbContext>>();
        services.AddSingleton<IBackupFilePathBuilder, BackupFilePathBuilder>();
        services.AddScoped(provider =>
        {
            ILogger<BackupService<UptimeHarborDbContext>> logger = provider
                .GetRequiredService<ILogger<BackupService<UptimeHarborDbContext>>>();
            IBackupFilePathBuilder backupFilePathBuilder = provider
                .GetRequiredService<IBackupFilePathBuilder>();
            string backupFolder = OperatingSystem.IsLinux()
                ? LINUX_SQL_SERVER_BACKUP_FOLDER
                : Path.Combine(AppContext.BaseDirectory, "Backups");
            return new BackupService<UptimeHarborDbContext>(
                connectionString,
                backupFolder,
                logger,
                backupFilePathBuilder);
        });
        services.AddScoped<IBackupService<UptimeHarborDbContext>>(provider =>
            provider.GetRequiredService<BackupService<UptimeHarborDbContext>>());

        services.AddScoped<IMonitoredServerRepository, MonitoredServerRepository>();
        services.AddScoped<IMonitoredContainerRepository, MonitoredContainerRepository>();
        services.AddScoped<ITelegramChatRepository, TelegramChatRepository>();
        services.AddScoped<INotificationBindingRepository, NotificationBindingRepository>();
        services.AddScoped<IDashboardSnapshotReadQuery, DashboardSnapshotReadQuery>();
        services.AddScoped<IMonitoringRecipientReadQuery, MonitoringRecipientReadQuery>();
        services.AddScoped<IUptimeHarborDbContextSession, UptimeHarborDbContextSession>();
        services.AddScoped<IMonitoringUnitOfWork, MonitoringUnitOfWork>();
        services.AddScoped<IObjectAdministrationUnitOfWork, ObjectAdministrationUnitOfWork>();
        services.AddScoped<INotificationSettingsUnitOfWork, NotificationSettingsUnitOfWork>();
        services.AddScoped<IProjectionRepository, ProjectionRepository>();
        services.AddScoped<IPendingOperationRepository, PendingOperationRepository>();
        services.AddScoped<IObservationCaptureRepository, ObservationCaptureRepository>();
        services.AddScoped<IDeliveryResultRepository, DeliveryResultRepository>();
        services.AddScoped<IProjectionApplyUnitOfWork, ProjectionApplyUnitOfWork>();
        services.AddScoped<IPendingControlUnitOfWork, PendingControlUnitOfWork>();
        services.AddScoped<UptimeHarbor.Application.Cluster.IRevisionPayloadProtection, RevisionPayloadProtection>();
        services.AddScoped<SnapshotProjectionUnitOfWork>();
        services.AddScoped<IObservationCaptureUnitOfWork, ObservationCaptureUnitOfWork>();
        services.AddScoped<IDeliveryResultUnitOfWork, DeliveryResultUnitOfWork>();
        services.AddScoped<IHistoryRetentionQuery, HistoryRetentionQuery>();
        services.AddScoped<IdentityUserAccountStore>();
        services.AddScoped<IUserAccountStore>(provider =>
            provider.GetRequiredService<IdentityUserAccountStore>());
        services.AddScoped<IInitialAdministratorStore>(provider =>
            provider.GetRequiredService<IdentityUserAccountStore>());
        services.AddScoped<InitialAdministratorBootstrapper>();
        services.AddScoped<IUserSignInService, UserSignInService>();
        services.AddScoped<IAccountCredentialService, AccountCredentialService>();
        services.AddSingleton<AcceptedAccountSession>();
        services.AddSingleton<LocalAccountLoginGate>();
        services.AddScoped<ILocalAccountLogin, LocalAccountLogin>();
        services.AddScoped<ILocalLoginUnitOfWork, LocalLoginUnitOfWork>();
        if (clusterOptions.Enabled)
        {
            services.AddScoped<IUserAccountStore, AcceptedUserAccountStore>();
            services.AddScoped<IUserSignInService, AcceptedUserSignInService>();
            services.AddScoped<IMonitoringRecipientReadQuery, AcceptedMonitoringRecipients>();
            services.AddDataProtection().SetApplicationName($"UptimeHarbor/{clusterOptions.NodeId:N}");
        }

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
        if (!clusterOptions.Enabled)
        {
            services.AddHostedService<MonitoringWorker>();
        }

        services.AddHttpClient<TelegramHttpNotificationSender>(client =>
        {
            client.BaseAddress = new Uri("https://api.telegram.org/");
            client.Timeout = TimeSpan.FromSeconds(30);
        }).RemoveAllLoggers();
        services.AddScoped<INotificationSender, ClusterNotificationSender>();
        services.AddScoped<NotificationOutboxStore>();
        services.AddSingleton<TelegramOutboxDispatcher>();
        if (!clusterOptions.Enabled)
        {
            services.AddHostedService<TelegramOutboxWorker>();
        }

        return services;
    }

    private static async Task ValidateEnabledUserAsync(CookieValidatePrincipalContext context)
    {
        ClusterOptions cluster = context.HttpContext.RequestServices
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<ClusterOptions>>().Value;
        if (cluster.Enabled)
        {
            AcceptedAccountSession sessions = context.HttpContext.RequestServices.GetRequiredService<AcceptedAccountSession>();
            if (!sessions.IsValid(context.Principal))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
            }

            return;
        }

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
