using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class ConflictRecordConfiguration : ProjectionConfiguration<ConflictRecord>
{
    protected override string TableName { get; } = "shared_conflicts";

    public override void Configure(EntityTypeBuilder<ConflictRecord> builder)
    {
        base.Configure(builder);
        Payload(builder.Property(entity => entity.ProtectedBasePayload));
        Payload(builder.Property(entity => entity.ProtectedAcceptedPayload));
        Payload(builder.Property(entity => entity.ProtectedProposedPayload));
        builder.HasIndex(entity => entity.OperationId).IsUnique();
        builder.HasIndex(entity => entity.ResolvedAtUtc);
        builder.ToTable(TableName, table => table.HasCheckConstraint("ck_shared_conflicts_revisions",
            "[BaseRevision] >= 0 AND [FirstAcceptedRevision] > 0"));
    }
}
