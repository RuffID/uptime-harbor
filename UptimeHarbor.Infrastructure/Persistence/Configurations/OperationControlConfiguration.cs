using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class OperationControlConfiguration : ProjectionConfiguration<OperationControl>
{
    protected override string TableName { get; } = "operation_controls";

    public override void Configure(EntityTypeBuilder<OperationControl> builder)
    {
        base.Configure(builder);
        Digest(builder.Property(entity => entity.TargetDigest));
        builder.Property(entity => entity.Kind).HasConversion<string>().HasMaxLength(16);
        builder.HasIndex(entity => entity.TargetOperationId).IsUnique();
        builder.ToTable(TableName, table => table.HasCheckConstraint("ck_operation_controls_replacement",
            "([Kind] = 'Replace' AND [ReplacementOperationId] IS NOT NULL) OR ([Kind] = 'Cancel' AND [ReplacementOperationId] IS NULL)"));
    }
}
