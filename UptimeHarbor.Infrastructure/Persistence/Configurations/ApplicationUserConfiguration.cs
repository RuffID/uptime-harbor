using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Infrastructure.Identity;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class ApplicationUserConfiguration : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(user => user.Id).ValueGeneratedNever();
        builder.Property(user => user.IsEnabled).IsRequired();
        builder.Property(user => user.UserName).HasMaxLength(256).IsRequired();
        builder.Property(user => user.NormalizedUserName).HasMaxLength(256).IsRequired();
        builder.HasIndex(user => user.NormalizedUserName).IsUnique().HasFilter(null);
        builder.Property(user => user.UserRevision).IsConcurrencyToken();
        builder.Property(user => user.PasswordHash).HasMaxLength(2048);
        builder.Property(user => user.SecurityStamp).HasMaxLength(256);
        builder.Property(user => user.ConcurrencyStamp).HasMaxLength(256);
        builder.ToTable("users", table =>
        {
            table.HasCheckConstraint("ck_users_revision", "[UserRevision] >= 0 AND [PasswordHashFormatVersion] = 1");
            table.HasCheckConstraint("ck_users_deleted", "[DeletedAtUtc] IS NULL OR [IsEnabled] = 0");
        });
    }
}
