using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class IncidentEventConfiguration : ProjectionConfiguration<IncidentEvent>
{
    protected override string TableName { get; } = "incident_events";

    public override void Configure(EntityTypeBuilder<IncidentEvent> builder)
    {
        base.Configure(builder);
        builder.Property(entity => entity.Kind).HasMaxLength(64).IsRequired();
        Digest(builder.Property(entity => entity.FrontierDigest));
        Payload(builder.Property(entity => entity.ProtectedPayload));
        builder.HasIndex(entity => new { entity.IncidentId, entity.Sequence }).IsUnique();
        builder.HasOne<SharedIncident>().WithMany().HasForeignKey(entity => entity.IncidentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasAlternateKey(entity => new { entity.Id, entity.IncidentId });
        builder.ToTable(TableName, table => table.HasCheckConstraint("ck_incident_events_sequence", "[Sequence] > 0"));
    }
}
