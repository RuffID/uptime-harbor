using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class ObservationCaptureCursorConfiguration : IEntityTypeConfiguration<ObservationCaptureCursor>
{
    public void Configure(EntityTypeBuilder<ObservationCaptureCursor> builder)
    {
        builder.ToTable("local_observation_cursors", table => table.HasCheckConstraint("ck_local_observation_cursors_sequence", "[LastSequence] >= 0"));
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.LastSequence).IsConcurrencyToken();
        builder.HasIndex(entity => new { entity.TargetId, entity.ObserverNodeId, entity.StorageGeneration,
            entity.CheckGeneration }).IsUnique();
    }
}
