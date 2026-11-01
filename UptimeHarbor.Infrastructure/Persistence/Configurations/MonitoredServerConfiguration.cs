using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain;
using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class MonitoredServerConfiguration : MonitoredObjectConfiguration<MonitoredServer>
{
    public const string NAME_UNIQUE_INDEX = "ux_monitored_servers_name";

    public override void Configure(EntityTypeBuilder<MonitoredServer> builder)
    {
        base.Configure(builder);

        builder.Property(server => server.StartedAtUtc);

        builder.Property(server => server.Name)
            .HasMaxLength(ModelConstraints.OBJECT_NAME_MAX_LENGTH)
            .IsRequired();
        builder.Property<string>("NormalizedName")
            .HasMaxLength(ModelConstraints.OBJECT_NAME_MAX_LENGTH)
            .IsRequired();
        builder.HasIndex("NormalizedName")
            .IsUnique()
            .HasDatabaseName(NAME_UNIQUE_INDEX);

        builder.ToTable(
            "monitored_servers",
            table =>
            {
                table.HasCheckConstraint(
                    "ck_monitored_servers_status",
                    "[Status] IN ('Unknown', 'Available', 'Unavailable')");
                table.HasCheckConstraint(
                    "ck_monitored_servers_endpoint_url",
                    "LOWER([EndpointUrl]) LIKE 'http://%' OR LOWER([EndpointUrl]) LIKE 'https://%'");
                table.HasCheckConstraint(
                    "ck_monitored_servers_consecutive_failures",
                    "[ConsecutiveFailures] >= 0");
            });
    }
}
