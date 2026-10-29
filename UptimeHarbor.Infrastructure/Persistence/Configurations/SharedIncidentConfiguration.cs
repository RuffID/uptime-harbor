using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class SharedIncidentConfiguration : ProjectionConfiguration<SharedIncident>
{
    protected override string TableName { get; } = "shared_incidents";

    public override void Configure(EntityTypeBuilder<SharedIncident> builder)
    {
        base.Configure(builder);
        builder.Property(entity => entity.Kind).HasConversion<string>().HasMaxLength(16);
        builder.Property(entity => entity.Completion).HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.TransitionKey).HasMaxLength(128).IsRequired();
        builder.HasIndex(entity => new { entity.TargetId, entity.CheckGeneration, entity.Kind, entity.TransitionKey }).IsUnique();
        builder.HasIndex(entity => entity.CompletedAtUtc);
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_shared_incidents_kind", "[Kind] IN ('Target','Coverage','Node')");
            table.HasCheckConstraint("ck_shared_incidents_completion",
                "([CompletedAtUtc] IS NULL AND [Completion] IS NULL) OR ([CompletedAtUtc] IS NOT NULL AND [Completion] IS NOT NULL AND [CompletedAtUtc] >= [OpenedAtUtc] AND [Completion] IN ('Recovered','SupersededConfiguration','Deleted'))");
        });
    }
}
