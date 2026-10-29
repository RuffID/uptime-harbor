using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class CapturedDeliveryResultConfiguration : IEntityTypeConfiguration<CapturedDeliveryResult>
{
    public void Configure(EntityTypeBuilder<CapturedDeliveryResult> builder)
    {
        builder.ToTable("local_delivery_results", table => table.HasCheckConstraint("ck_local_delivery_results_outcome",
            "[Outcome] IN ('Succeeded','Failed','CancelledBeforeTransport')"));
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.Outcome).HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.FailureKind).HasMaxLength(64);
    }
}
