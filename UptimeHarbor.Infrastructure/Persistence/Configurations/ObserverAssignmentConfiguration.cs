using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class ObserverAssignmentConfiguration : ProjectionConfiguration<ObserverAssignment>
{
    protected override string TableName { get; } = "observer_assignments";

    public override void Configure(EntityTypeBuilder<ObserverAssignment> builder)
    {
        base.Configure(builder);
        builder.HasIndex(entity => new { entity.TargetId, entity.ObserverNodeId }).IsUnique();
        builder.HasOne<ClusterMember>().WithMany().HasForeignKey(entity => entity.ObserverNodeId).OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(TableName, table => table.HasCheckConstraint("ck_observer_assignments_revision", "[ConfigRevision] > 0"));
    }
}
