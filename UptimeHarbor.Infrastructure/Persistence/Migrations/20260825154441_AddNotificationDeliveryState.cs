using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UptimeHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationDeliveryState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OutageNotificationSent",
                table: "monitored_servers");

            migrationBuilder.DropColumn(
                name: "OutageNotificationSent",
                table: "monitored_containers");

            migrationBuilder.AddColumn<Guid>(
                name: "OutageDeliveredIncidentId",
                table: "notification_bindings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RecoveryDeliveredIncidentId",
                table: "notification_bindings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CurrentIncidentId",
                table: "monitored_servers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CurrentIncidentId",
                table: "monitored_containers",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OutageDeliveredIncidentId",
                table: "notification_bindings");

            migrationBuilder.DropColumn(
                name: "RecoveryDeliveredIncidentId",
                table: "notification_bindings");

            migrationBuilder.DropColumn(
                name: "CurrentIncidentId",
                table: "monitored_servers");

            migrationBuilder.DropColumn(
                name: "CurrentIncidentId",
                table: "monitored_containers");

            migrationBuilder.AddColumn<bool>(
                name: "OutageNotificationSent",
                table: "monitored_servers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "OutageNotificationSent",
                table: "monitored_containers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
