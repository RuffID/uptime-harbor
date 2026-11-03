using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class LocalStepRecordConfiguration : IEntityTypeConfiguration<LocalStepRecord>
{
    public void Configure(EntityTypeBuilder<LocalStepRecord> builder)
    {
        builder.ToTable("local_attempt_steps");
        builder.HasKey(item => item.Id);
        builder.HasIndex(item => item.AttemptId);
        builder.Property(item => item.Payload).IsRequired();
    }
}
