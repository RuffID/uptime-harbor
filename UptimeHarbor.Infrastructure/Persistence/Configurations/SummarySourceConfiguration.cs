using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class SummarySourceConfiguration : ProjectionConfiguration<SummarySource>
{
    protected override string TableName { get; } = "summary_sources";

    public override void Configure(EntityTypeBuilder<SummarySource> builder)
    {
        base.Configure(builder);
        builder.HasIndex(entity => new { entity.SummaryId, entity.SourceEventId, entity.SourceIntentId }).IsUnique();
        builder.HasIndex(entity => entity.SourceIntentId).IsUnique();
        builder.HasOne<DeliverySummary>().WithMany().HasForeignKey(entity => entity.SummaryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<IncidentEvent>().WithMany().HasForeignKey(entity => entity.SourceEventId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DeliveryIntent>().WithMany().HasForeignKey(entity => entity.SourceIntentId).OnDelete(DeleteBehavior.Restrict);
    }
}
