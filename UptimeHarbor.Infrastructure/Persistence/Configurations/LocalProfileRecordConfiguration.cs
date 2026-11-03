using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class LocalProfileRecordConfiguration : IEntityTypeConfiguration<LocalProfileRecord>
{
    public void Configure(EntityTypeBuilder<LocalProfileRecord> builder)
    {
        builder.ToTable("local_installation_profiles", table =>
            table.HasCheckConstraint("ck_local_installation_singleton", "[Id] = 1"));
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();
        builder.HasIndex(item => item.InstallIdentity).IsUnique();
        builder.Property(item => item.Revision).IsConcurrencyToken();
        builder.Property(item => item.Payload).IsRequired();
    }
}
