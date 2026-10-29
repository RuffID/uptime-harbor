using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class ObservationFrontierConfiguration : ProjectionConfiguration<ObservationFrontier>
{
    protected override string TableName { get; } = "observation_frontiers";

    public override void Configure(EntityTypeBuilder<ObservationFrontier> builder)
    {
        base.Configure(builder);
        builder.HasIndex(entity => new { entity.TargetId, entity.ObserverNodeId, entity.StorageGeneration,
            entity.CheckGeneration }).IsUnique();
        builder.ToTable(TableName, table => table.HasCheckConstraint("ck_observation_frontiers_sequence", "[AcceptedSequence] >= 0"));
    }
}
