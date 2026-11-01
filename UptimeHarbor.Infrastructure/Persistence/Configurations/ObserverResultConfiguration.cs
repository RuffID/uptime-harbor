using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class ObserverResultConfiguration : ProjectionConfiguration<ObserverResult>
{
    protected override string TableName { get; } = "observer_results";

    public override void Configure(EntityTypeBuilder<ObserverResult> builder)
    {
        base.Configure(builder);
        Digest(builder.Property(entity => entity.Digest));
        builder.Property(entity => entity.EndpointName).HasMaxLength(ModelConstraints.OBJECT_NAME_MAX_LENGTH);
        builder.Property(entity => entity.FailureKind).HasMaxLength(64);
        builder.HasIndex(entity => new { entity.TargetId, entity.ObserverNodeId, entity.StorageGeneration,
            entity.CheckGeneration, entity.Sequence }).IsUnique();
        builder.HasIndex(entity => new { entity.TargetId, entity.CheckGeneration, entity.CheckedAtUtc });
        builder.ToTable(TableName, table => table.HasCheckConstraint("ck_observer_results_values",
            "[ConfigRevision] > 0 AND [Sequence] > 0 AND [ConsecutiveFailures] >= 0 AND [DurationMilliseconds] >= 0"));
    }
}
