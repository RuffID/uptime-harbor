using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UptimeHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitoredServerName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "monitored_servers",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedName",
                table: "monitored_servers",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false);

            migrationBuilder.CreateIndex(
                name: "ux_monitored_servers_name",
                table: "monitored_servers",
                column: "NormalizedName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_monitored_servers_name",
                table: "monitored_servers");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "monitored_servers");

            migrationBuilder.DropColumn(
                name: "NormalizedName",
                table: "monitored_servers");
        }
    }
}
