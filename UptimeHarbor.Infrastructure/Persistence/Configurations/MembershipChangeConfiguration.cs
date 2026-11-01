using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class MembershipChangeConfiguration : ProjectionConfiguration<MembershipChange>
{
    protected override string TableName { get; } = "membership_changes";

    public override void Configure(EntityTypeBuilder<MembershipChange> builder)
    {
        base.Configure(builder);
        builder.Property(entity => entity.State).HasConversion<string>().HasMaxLength(32);
        builder.HasIndex(entity => new { entity.ClusterId, entity.ClusterGeneration }).IsUnique()
            .HasFilter("[State] IN ('Requested','CatchingUp','ProtocolCommitted')");
        builder.ToTable(TableName, table => table.HasCheckConstraint("ck_membership_changes_state",
            "[State] IN ('Requested','CatchingUp','ProtocolCommitted','Activated','Removed') AND [ExpectedVersion] >= 0"));
    }
}
