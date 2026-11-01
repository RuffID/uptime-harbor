using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class AppliedJournalReceiptConfiguration : ProjectionConfiguration<AppliedJournalReceipt>
{
    protected override string TableName { get; } = "applied_journal_receipts";

    public override void Configure(EntityTypeBuilder<AppliedJournalReceipt> builder)
    {
        base.Configure(builder);
        Digest(builder.Property(entity => entity.Digest));
        builder.HasIndex(entity => new { entity.ClusterId, entity.ClusterGeneration, entity.LogIndex }).IsUnique();
    }
}
