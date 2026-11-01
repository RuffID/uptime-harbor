using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class CapturedObservationConfiguration : IEntityTypeConfiguration<CapturedObservation>
{
    public void Configure(EntityTypeBuilder<CapturedObservation> builder)
    {
        builder.ToTable("local_captured_observations", table => table.HasCheckConstraint("ck_local_captured_observations_sequence", "[Sequence] > 0"));
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.Digest).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(entity => entity.Payload).HasMaxLength(StorageGuards.MAX_PAYLOAD_BYTES).IsRequired();
        builder.HasIndex(entity => new { entity.StreamId, entity.Sequence }).IsUnique();
        builder.HasOne<ObservationCaptureCursor>().WithMany().HasForeignKey(entity => entity.StreamId).OnDelete(DeleteBehavior.Restrict);
    }
}
