using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class TargetObservationStateConfiguration : ProjectionConfiguration<TargetObservationState>
{
    protected override string TableName { get; } = "target_observation_states";

    public override void Configure(EntityTypeBuilder<TargetObservationState> builder)
    {
        base.Configure(builder);
        Digest(builder.Property(entity => entity.FrontierDigest));
        Payload(builder.Property(entity => entity.ProblemDirectionsJson));
        builder.Property(entity => entity.Availability).HasConversion<string>().HasMaxLength(16);
        builder.Property(entity => entity.Coverage).HasConversion<string>().HasMaxLength(16);
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_target_observation_states_availability", "[Availability] IN ('Unknown','Available','Partial','Unavailable')");
            table.HasCheckConstraint("ck_target_observation_states_coverage", "[Coverage] IN ('Initializing','Complete','Degraded','Lost')");
        });
    }
}
