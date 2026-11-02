using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UptimeHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UnifyMonitoredServersAndApplications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [monitored_servers] s INNER JOIN [monitored_containers] a ON a.[Id] = s.[Id])
                    THROW 51000, 'MonitoringTargetIdCollision', 1;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_notification_bindings_monitored_containers_ContainerId",
                table: "notification_bindings");

            migrationBuilder.DropForeignKey(
                name: "FK_notification_bindings_monitored_servers_ServerId",
                table: "notification_bindings");

            migrationBuilder.DropIndex(
                name: "ux_monitored_servers_name",
                table: "monitored_servers");

            migrationBuilder.AddColumn<Guid>(
                name: "ServerId",
                table: "monitored_servers",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "monitored_servers",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE [monitored_servers] SET [Type] = N'Server';

                INSERT INTO [monitored_servers]
                    ([Id], [Name], [NormalizedName], [Type], [ServerId], [CheckGeneration], [ConfigRevision],
                     [ConsecutiveFailures], [CurrentIncidentId], [Description], [EndpointUrl], [IsMonitoringEnabled],
                     [LastCheckedAtUtc], [LastKnownName], [LastSuccessfulCheckAtUtc], [StartedAtUtc], [Status], [Version])
                SELECT [Id],
                    N'Приложение ' + LOWER(REPLACE(CONVERT(nvarchar(36), [Id]), N'-', N'')),
                    N'ПРИЛОЖЕНИЕ ' + UPPER(REPLACE(CONVERT(nvarchar(36), [Id]), N'-', N'')),
                    N'Application', [ServerId], [CheckGeneration], [ConfigRevision],
                    [ConsecutiveFailures], [CurrentIncidentId], [Description], [EndpointUrl], [IsMonitoringEnabled],
                    [LastCheckedAtUtc], [LastKnownName], [LastSuccessfulCheckAtUtc], [StartedAtUtc], [Status], [Version]
                FROM [monitored_containers];
                """);

            migrationBuilder.DropTable(
                name: "monitored_containers");

            migrationBuilder.AlterColumn<string>(
                name: "Type",
                table: "monitored_servers",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_monitored_servers_ServerId",
                table: "monitored_servers",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "ux_monitored_servers_name",
                table: "monitored_servers",
                column: "NormalizedName",
                unique: true,
                filter: "[Type] = 'Server'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_monitored_servers_parent",
                table: "monitored_servers",
                sql: "[ServerId] IS NULL OR ([Type] = 'Application' AND [ServerId] <> [Id])");

            migrationBuilder.AddForeignKey(
                name: "FK_monitored_servers_monitored_servers_ServerId",
                table: "monitored_servers",
                column: "ServerId",
                principalTable: "monitored_servers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_notification_bindings_monitored_servers_ContainerId",
                table: "notification_bindings",
                column: "ContainerId",
                principalTable: "monitored_servers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_notification_bindings_monitored_servers_ServerId",
                table: "notification_bindings",
                column: "ServerId",
                principalTable: "monitored_servers",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Прежняя схема не хранит административные имена приложений: не терять их при откате.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [monitored_servers] WHERE [Type] NOT IN (N'Server', N'Application'))
                    THROW 51000, 'UnsupportedMonitoringTargetType', 1;
                IF EXISTS (
                    SELECT 1 FROM [monitored_servers]
                    WHERE [Type] = N'Application' AND (
                        [Name] COLLATE Latin1_General_100_BIN2 <>
                            N'Приложение ' + LOWER(REPLACE(CONVERT(nvarchar(36), [Id]), N'-', N'')) COLLATE Latin1_General_100_BIN2
                        OR [NormalizedName] COLLATE Latin1_General_100_BIN2 <>
                            N'ПРИЛОЖЕНИЕ ' + UPPER(REPLACE(CONVERT(nvarchar(36), [Id]), N'-', N'')) COLLATE Latin1_General_100_BIN2))
                    THROW 51000, 'ApplicationNamesCannotBeDowngraded', 1;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_monitored_servers_monitored_servers_ServerId",
                table: "monitored_servers");

            migrationBuilder.DropForeignKey(
                name: "FK_notification_bindings_monitored_servers_ContainerId",
                table: "notification_bindings");

            migrationBuilder.DropForeignKey(
                name: "FK_notification_bindings_monitored_servers_ServerId",
                table: "notification_bindings");

            migrationBuilder.DropIndex(
                name: "IX_monitored_servers_ServerId",
                table: "monitored_servers");

            migrationBuilder.DropIndex(
                name: "ux_monitored_servers_name",
                table: "monitored_servers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_monitored_servers_parent",
                table: "monitored_servers");

            migrationBuilder.CreateTable(
                name: "monitored_containers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CheckGeneration = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfigRevision = table.Column<long>(type: "bigint", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    CurrentIncidentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    EndpointUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    IsMonitoringEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LastCheckedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastKnownName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LastSuccessfulCheckAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_monitored_containers", x => x.Id);
                    table.CheckConstraint("ck_monitored_containers_consecutive_failures", "[ConsecutiveFailures] >= 0");
                    table.CheckConstraint("ck_monitored_containers_endpoint_url", "LOWER([EndpointUrl]) LIKE 'http://%' OR LOWER([EndpointUrl]) LIKE 'https://%'");
                    table.CheckConstraint("ck_monitored_containers_status", "[Status] IN ('Unknown', 'Available', 'Unavailable')");
                    table.ForeignKey(
                        name: "FK_monitored_containers_monitored_servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "monitored_servers",
                        principalColumn: "Id");
                });

            migrationBuilder.Sql("""
                INSERT INTO [monitored_containers]
                    ([Id], [CheckGeneration], [ConfigRevision], [ConsecutiveFailures], [CurrentIncidentId],
                     [Description], [EndpointUrl], [IsMonitoringEnabled], [LastCheckedAtUtc], [LastKnownName],
                     [LastSuccessfulCheckAtUtc], [ServerId], [StartedAtUtc], [Status], [Version])
                SELECT [Id], [CheckGeneration], [ConfigRevision], [ConsecutiveFailures], [CurrentIncidentId],
                    [Description], [EndpointUrl], [IsMonitoringEnabled], [LastCheckedAtUtc], [LastKnownName],
                    [LastSuccessfulCheckAtUtc], [ServerId], [StartedAtUtc], [Status], [Version]
                FROM [monitored_servers] WHERE [Type] = N'Application';

                DELETE FROM [monitored_servers] WHERE [Type] = N'Application';
                """);

            migrationBuilder.DropColumn(
                name: "ServerId",
                table: "monitored_servers");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "monitored_servers");

            migrationBuilder.CreateIndex(
                name: "ux_monitored_servers_name",
                table: "monitored_servers",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_monitored_containers_ServerId",
                table: "monitored_containers",
                column: "ServerId");

            migrationBuilder.AddForeignKey(
                name: "FK_notification_bindings_monitored_containers_ContainerId",
                table: "notification_bindings",
                column: "ContainerId",
                principalTable: "monitored_containers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_notification_bindings_monitored_servers_ServerId",
                table: "notification_bindings",
                column: "ServerId",
                principalTable: "monitored_servers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
