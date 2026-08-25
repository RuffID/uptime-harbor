using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Domain.Access;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Domain.Notifications;
using UptimeHarbor.Infrastructure.Identity;

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

        builder.ApplyConfigurationsFromAssembly(typeof(UptimeHarborDbContext).Assembly);
    }
}
