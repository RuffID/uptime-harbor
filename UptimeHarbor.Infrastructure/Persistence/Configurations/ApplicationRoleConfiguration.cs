using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Infrastructure.Identity;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class ApplicationRoleConfiguration : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<ApplicationRole>
{
    public void Configure(EntityTypeBuilder<ApplicationRole> builder)
    {
        builder.Property(role => role.Id).ValueGeneratedNever();
        builder.Property(role => role.Name).HasMaxLength(256).IsRequired();
        builder.Property(role => role.NormalizedName).HasMaxLength(256).IsRequired();
    }
}
