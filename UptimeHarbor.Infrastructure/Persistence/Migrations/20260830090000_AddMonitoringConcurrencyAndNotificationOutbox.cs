using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UptimeHarbor.Infrastructure.Persistence.Migrations;

[DbContext(typeof(UptimeHarborDbContext))]
[Migration("20260830090000_AddMonitoringConcurrencyAndNotificationOutbox")]
public class AddMonitoringConcurrencyAndNotificationOutbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "Version",
            table: "monitored_servers",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<long>(
            name: "Version",
            table: "monitored_containers",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.CreateTable(
            name: "notification_outbox_jobs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                AttemptCount = table.Column<int>(type: "integer", nullable: false),
                NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                LockedUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                FailedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_notification_outbox_jobs", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_notification_outbox_jobs_CompletedAtUtc_FailedAtUtc_NextAttemptAtUtc",
            table: "notification_outbox_jobs",
            columns: new[] { "CompletedAtUtc", "FailedAtUtc", "NextAttemptAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "notification_outbox_jobs");

        migrationBuilder.DropColumn(name: "Version", table: "monitored_servers");

        migrationBuilder.DropColumn(name: "Version", table: "monitored_containers");
    }
}
