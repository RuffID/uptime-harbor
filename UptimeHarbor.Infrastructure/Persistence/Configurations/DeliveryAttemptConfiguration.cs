using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class DeliveryAttemptConfiguration : ProjectionConfiguration<DeliveryAttempt>
{
    protected override string TableName { get; } = "delivery_attempts";

    public override void Configure(EntityTypeBuilder<DeliveryAttempt> builder)
    {
        base.Configure(builder);
        builder.Property(entity => entity.Outcome).HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.FailureKind).HasMaxLength(64);
        builder.HasIndex(entity => new { entity.IntentId, entity.Number }).IsUnique();
        builder.HasOne<DeliveryIntent>().WithMany().HasForeignKey(entity => entity.IntentId).OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_delivery_attempts_number", "[Number] BETWEEN 1 AND 5");
            table.HasCheckConstraint("ck_delivery_attempts_outcome", "[Outcome] IN ('Unknown','Succeeded','Failed','CancelledBeforeTransport')");
        });
    }
}
