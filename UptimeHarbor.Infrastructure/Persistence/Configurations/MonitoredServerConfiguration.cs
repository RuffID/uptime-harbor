using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class MonitoredServerConfiguration : MonitoredObjectConfiguration<MonitoredServer>
{
    public override void Configure(EntityTypeBuilder<MonitoredServer> builder)
    {
        base.Configure(builder);

        builder.ToTable(
            "monitored_servers",
            table =>
            {
                table.HasCheckConstraint(
                    "ck_monitored_servers_status",
                    "\"Status\" IN ('Unknown', 'Available', 'Unavailable')");
                table.HasCheckConstraint(
                    "ck_monitored_servers_endpoint_url",
                    "lower(\"EndpointUrl\") LIKE 'http://%' OR lower(\"EndpointUrl\") LIKE 'https://%'");
                table.HasCheckConstraint(
                    "ck_monitored_servers_consecutive_failures",
                    "\"ConsecutiveFailures\" >= 0");
            });
    }
}
