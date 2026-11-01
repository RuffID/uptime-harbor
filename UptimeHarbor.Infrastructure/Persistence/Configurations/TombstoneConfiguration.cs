using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class TombstoneConfiguration : ProjectionConfiguration<Tombstone>
{
    protected override string TableName { get; } = "shared_tombstones";

    public override void Configure(EntityTypeBuilder<Tombstone> builder)
    {
        base.Configure(builder);
        builder.Property(entity => entity.Kind).HasConversion<string>().HasMaxLength(32);
        builder.ToTable(TableName, table => table.HasCheckConstraint("ck_shared_tombstones_kind",
            "[Kind] IN ('Server','Container','Chat','Binding','Account','Node','ObserverAssignment','NodeChatRoute')"));
    }
}
