using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Identity;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class ApplicationUserConfiguration : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(user => user.Id).ValueGeneratedNever();
        builder.Property(user => user.IsEnabled).IsRequired();
    }
}
