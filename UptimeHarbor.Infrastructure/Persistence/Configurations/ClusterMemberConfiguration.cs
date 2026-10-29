using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class ClusterMemberConfiguration : ProjectionConfiguration<ClusterMember>
{
    protected override string TableName { get; } = "cluster_members";

    public override void Configure(EntityTypeBuilder<ClusterMember> builder)
    {
        base.Configure(builder);
        builder.Property(entity => entity.PeerUri).HasMaxLength(2048).IsRequired();
        builder.Property(entity => entity.RaftMemberId).HasMaxLength(128).IsUnicode(false).IsRequired();
        builder.HasIndex(entity => entity.RaftMemberId).IsUnique();
        builder.ToTable(TableName, table => table.HasCheckConstraint("ck_cluster_members_version", "[MembershipVersion] >= 0"));
    }
}
