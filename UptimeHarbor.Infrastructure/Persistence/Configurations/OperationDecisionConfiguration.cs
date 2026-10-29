using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class OperationDecisionConfiguration : ProjectionConfiguration<OperationDecision>
{
    protected override string TableName { get; } = "operation_decisions";

    public override void Configure(EntityTypeBuilder<OperationDecision> builder)
    {
        base.Configure(builder);
        Digest(builder.Property(entity => entity.Digest));
        builder.Property(entity => entity.Outcome).HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.ReasonCode).HasMaxLength(64);
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_operation_decisions_outcome",
                "[Outcome] IN ('Committed','Conflict','Rejected','Cancelled','Replaced')");
            table.HasCheckConstraint("ck_operation_decisions_replacement",
                "([Outcome] = 'Replaced' AND [ReplacedBy] IS NOT NULL) OR ([Outcome] <> 'Replaced' AND [ReplacedBy] IS NULL)");
            table.HasCheckConstraint("ck_operation_decisions_conflict",
                "([Outcome] = 'Conflict' AND [ConflictId] IS NOT NULL) OR ([Outcome] <> 'Conflict' AND [ConflictId] IS NULL)");
        });
    }
}
