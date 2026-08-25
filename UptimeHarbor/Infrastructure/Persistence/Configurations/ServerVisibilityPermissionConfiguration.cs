using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Access;
using UptimeHarbor.Domain.Identity;
using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class ServerVisibilityPermissionConfiguration : IEntityTypeConfiguration<ServerVisibilityPermission>
{
    public void Configure(EntityTypeBuilder<ServerVisibilityPermission> builder)
    {
        builder.ToTable("server_visibility_permissions");
        builder.HasKey(permission => new { permission.UserId, permission.ServerId });
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(permission => permission.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<MonitoredServer>()
            .WithMany()
            .HasForeignKey(permission => permission.ServerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
