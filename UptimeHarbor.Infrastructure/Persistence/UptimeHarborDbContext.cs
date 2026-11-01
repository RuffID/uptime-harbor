using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Domain.Access;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Domain.Notifications;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Infrastructure.Persistence.Configurations;

namespace UptimeHarbor.Infrastructure.Persistence;

public class UptimeHarborDbContext(
    DbContextOptions<UptimeHarborDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options), IDataProtectionKeyContext
{
    public DbSet<MonitoredServer> MonitoredServers => Set<MonitoredServer>();

    public DbSet<MonitoredContainer> MonitoredContainers => Set<MonitoredContainer>();

    public DbSet<ServerVisibilityPermission> ServerVisibilityPermissions => Set<ServerVisibilityPermission>();

    public DbSet<ContainerVisibilityPermission> ContainerVisibilityPermissions => Set<ContainerVisibilityPermission>();

    public DbSet<TelegramChat> TelegramChats => Set<TelegramChat>();

    public DbSet<NotificationBinding> NotificationBindings => Set<NotificationBinding>();

    public DbSet<NotificationOutboxJob> NotificationOutboxJobs => Set<NotificationOutboxJob>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder
            .Properties<DateTimeOffset>()
            .HaveConversion<UtcDateTimeOffsetConverter>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>().ToTable("users");
        builder.Entity<ApplicationRole>().ToTable("roles");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        builder.Entity<DataProtectionKey>().ToTable("data_protection_keys");
        builder.Ignore<MonitoredObject>();
        builder.Ignore<ProjectionRecord>();

        // Контракты совместимости не участвуют в модели и не подключают прежние конфигурации.
        builder.Ignore<ServerVisibilityPermission>();
        builder.Ignore<ContainerVisibilityPermission>();

        builder.ApplyConfigurationsFromAssembly(
            typeof(UptimeHarborDbContext).Assembly,
            configurationType => configurationType != typeof(ServerVisibilityPermissionConfiguration)
                && configurationType != typeof(ContainerVisibilityPermissionConfiguration));

        if (Database.IsSqlServer())
        {
            builder.Entity<ApplicationUser>().Property(user => user.NormalizedUserName)
                .UseCollation("Latin1_General_100_BIN2");
            builder.Entity<ApplicationRole>().Property(role => role.NormalizedName)
                .UseCollation("Latin1_General_100_BIN2");
        }

        builder.Entity<ApplicationRole>().ToTable("roles", table => table.HasCheckConstraint(
            "ck_roles_administrator", "[Name] = 'Administrator' AND [NormalizedName] = 'ADMINISTRATOR'"));
    }
}
