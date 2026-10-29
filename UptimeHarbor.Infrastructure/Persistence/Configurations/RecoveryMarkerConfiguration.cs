using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class RecoveryMarkerConfiguration : ProjectionConfiguration<RecoveryMarker>
{
    protected override string TableName { get; } = "recovery_markers";

    public override void Configure(EntityTypeBuilder<RecoveryMarker> builder)
    {
        base.Configure(builder);
        Digest(builder.Property(entity => entity.BaselineDigest));
    }
}
