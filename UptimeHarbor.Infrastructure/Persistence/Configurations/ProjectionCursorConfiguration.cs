using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class ProjectionCursorConfiguration : IEntityTypeConfiguration<ProjectionCursor>
{
    public void Configure(EntityTypeBuilder<ProjectionCursor> builder)
    {
        builder.ToTable("projection_cursors", table => table.HasCheckConstraint("ck_projection_cursors_position",
            "[ClusterGeneration] > 0 AND [LogIndex] >= 0 AND [Term] >= 0"));
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.LogIndex).IsConcurrencyToken();
        builder.Property(entity => entity.Digest).HasMaxLength(64).IsFixedLength().IsUnicode(false);
    }
}
