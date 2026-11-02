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
        builder.Property(server => server.Type)
            .HasConversion(type => TypeCode(type), code => ParseTypeCode(code))
            .HasMaxLength(32)
            .IsRequired();
        builder.HasOne<MonitoredServer>()
            .WithMany()
            .HasForeignKey(server => server.ServerId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.Property(server => server.Name)
            .HasMaxLength(ModelConstraints.OBJECT_NAME_MAX_LENGTH)
            .IsRequired();
        builder.Property<string>("NormalizedName")
            .HasMaxLength(ModelConstraints.OBJECT_NAME_MAX_LENGTH)
            .IsRequired();
        builder.HasIndex("NormalizedName")
            .IsUnique()
            .HasFilter("[Type] = 'Server'")
            .HasDatabaseName(NAME_UNIQUE_INDEX);

        builder.ToTable(
            "monitored_servers",
            table =>
            {
                table.HasCheckConstraint(
                    "ck_monitored_servers_parent",
                    "[ServerId] IS NULL OR ([Type] = 'Application' AND [ServerId] <> [Id])");
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

    private static string TypeCode(ServerType type)
    {
        switch (type)
        {
            case ServerType.Server:
                return "Server";
            case ServerType.Application:
                return "Application";
            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    private static ServerType ParseTypeCode(string code)
    {
        switch (code)
        {
            case "Server":
                return ServerType.Server;
            case "Application":
                return ServerType.Application;
            default:
                throw new InvalidOperationException("Unknown monitoring target type code.");
        }
    }
}
