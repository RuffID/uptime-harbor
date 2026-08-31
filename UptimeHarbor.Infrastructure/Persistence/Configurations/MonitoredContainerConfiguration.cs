using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class MonitoredContainerConfiguration : MonitoredObjectConfiguration<MonitoredContainer>
{
    public override void Configure(EntityTypeBuilder<MonitoredContainer> builder)
    {
        base.Configure(builder);

        builder.ToTable(
            "monitored_containers",
            table =>
            {
                table.HasCheckConstraint(
                    "ck_monitored_containers_status",
                    "[Status] IN ('Unknown', 'Available', 'Unavailable')");
                table.HasCheckConstraint(
                    "ck_monitored_containers_endpoint_url",
                    "LOWER([EndpointUrl]) LIKE 'http://%' OR LOWER([EndpointUrl]) LIKE 'https://%'");
                table.HasCheckConstraint(
                    "ck_monitored_containers_consecutive_failures",
                    "[ConsecutiveFailures] >= 0");
            });
        builder.HasAlternateKey(entity => new { entity.ServerId, entity.Id })
            .HasName("ak_monitored_containers_server_id_id");
        builder.HasOne<MonitoredServer>()
            .WithMany()
            .HasForeignKey(entity => entity.ServerId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.Property(entity => entity.StartedAtUtc);
    }
}
