using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class LocalAttemptRecordConfiguration : IEntityTypeConfiguration<LocalAttemptRecord>
{
    public void Configure(EntityTypeBuilder<LocalAttemptRecord> builder)
    {
        builder.ToTable("local_onboarding_attempts");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Revision).IsConcurrencyToken();
        builder.Property(item => item.Payload).IsRequired();
    }
}
