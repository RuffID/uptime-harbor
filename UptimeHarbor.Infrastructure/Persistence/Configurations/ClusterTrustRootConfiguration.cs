using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class ClusterTrustRootConfiguration : ProjectionConfiguration<ClusterTrustRoot>
{
    protected override string TableName { get; } = "cluster_trust_roots";

    public override void Configure(EntityTypeBuilder<ClusterTrustRoot> builder)
    {
        base.Configure(builder);
        Digest(builder.Property(entity => entity.Fingerprint));
        builder.Property(entity => entity.PublicCertificate).HasMaxLength(16384).IsRequired();
        builder.HasIndex(entity => entity.Fingerprint).IsUnique();
    }
}
