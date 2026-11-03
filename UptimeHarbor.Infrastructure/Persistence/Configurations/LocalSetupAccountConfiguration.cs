using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class LocalSetupAccountConfiguration : IEntityTypeConfiguration<LocalSetupAccount>
{
    public void Configure(EntityTypeBuilder<LocalSetupAccount> builder)
    {
        builder.ToTable("local_setup_accounts");
        builder.HasKey(item => item.Id);
        builder.HasIndex(item => item.CreatedForInstallIdentity).IsUnique();
        builder.Property(item => item.NormalizedLogin).HasMaxLength(256).IsRequired();
        builder.Property(item => item.PasswordHash).HasMaxLength(1024);
        builder.Property(item => item.Stamp).HasMaxLength(64).IsRequired();
        builder.Property(item => item.Revision).IsConcurrencyToken();
    }
}
