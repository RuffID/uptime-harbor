using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Access;
using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class ContainerVisibilityPermissionConfiguration : IEntityTypeConfiguration<ContainerVisibilityPermission>
{
    public void Configure(EntityTypeBuilder<ContainerVisibilityPermission> builder)
    {
        builder.ToTable("container_visibility_permissions");
        builder.HasKey(permission => new { permission.UserId, permission.ContainerId });
        builder.HasOne<ServerVisibilityPermission>()
            .WithMany()
            .HasForeignKey(permission => new { permission.UserId, permission.ServerId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<MonitoredContainer>()
            .WithMany()
            .HasForeignKey(permission => new { permission.ServerId, permission.ContainerId })
            .HasPrincipalKey(container => new { container.ServerId, container.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
