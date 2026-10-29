using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class DeliverySummaryConfiguration : ProjectionConfiguration<DeliverySummary>
{
    protected override string TableName { get; } = "delivery_summaries";

    public override void Configure(EntityTypeBuilder<DeliverySummary> builder)
    {
        base.Configure(builder);
        builder.Property(entity => entity.Text).HasMaxLength(3500).IsRequired();
        builder.HasIndex(entity => new { entity.PlanId, entity.ChatId, entity.PartNumber }).IsUnique();
        builder.HasIndex(entity => entity.IntentId).IsUnique();
        builder.HasOne<DeliveryIntent>().WithMany().HasForeignKey(entity => entity.IntentId).OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(TableName, table => table.HasCheckConstraint("ck_delivery_summaries_part",
            "[PartNumber] > 0 AND [CutIndex] > 0 AND [CutIndex] <= [LogIndex]"));
    }
}
