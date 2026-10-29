using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class LocalNodeStateConfiguration : IEntityTypeConfiguration<LocalNodeState>
{
    public void Configure(EntityTypeBuilder<LocalNodeState> builder)
    {
        builder.ToTable("local_node_state", table => table.HasCheckConstraint("ck_local_node_state_indices",
            "[ClusterGeneration] > 0 AND [SnapshotCutIndex] >= 0 AND [ReceivedIndex] >= 0 AND [CommittedIndex] >= 0 AND [MinReplayIndex] >= 0"));
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.WalStoreIdentity).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.SnapshotDigest).HasMaxLength(64).IsFixedLength().IsUnicode(false);
    }
}
