using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Identity;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class ApplicationRoleConfiguration : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<ApplicationRole>
{
    public void Configure(EntityTypeBuilder<ApplicationRole> builder)
    {
        builder.Property(role => role.Id).ValueGeneratedNever();
    }
}
