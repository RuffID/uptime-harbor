using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class NotificationOutboxJobConfiguration : IEntityTypeConfiguration<NotificationOutboxJob>
{
    public void Configure(EntityTypeBuilder<NotificationOutboxJob> builder)
    {
        builder.ToTable("notification_outbox_jobs");
        builder.HasKey(job => job.Id);
        builder.Property(job => job.Id).ValueGeneratedNever();
        builder.Property(job => job.PayloadJson)
            .HasColumnType("jsonb")
            .IsRequired();
        builder.HasIndex(job => new
        {
            job.CompletedAtUtc,
            job.FailedAtUtc,
            job.NextAttemptAtUtc
        });
    }
}
