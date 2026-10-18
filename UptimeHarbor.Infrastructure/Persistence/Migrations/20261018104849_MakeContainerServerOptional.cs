using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UptimeHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MakeContainerServerOptional : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_container_visibility_permissions_monitored_containers_ServerId_ContainerId",
                table: "container_visibility_permissions");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_monitored_containers_server_id_id",
                table: "monitored_containers");

            migrationBuilder.DropIndex(
                name: "IX_container_visibility_permissions_ServerId_ContainerId",
                table: "container_visibility_permissions");

            migrationBuilder.AlterColumn<Guid>(
                name: "ServerId",
                table: "monitored_containers",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.CreateIndex(
                name: "IX_monitored_containers_ServerId",
                table: "monitored_containers",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_container_visibility_permissions_ContainerId",
                table: "container_visibility_permissions",
                column: "ContainerId");

            migrationBuilder.AddForeignKey(
                name: "FK_container_visibility_permissions_monitored_containers_ContainerId",
                table: "container_visibility_permissions",
                column: "ContainerId",
                principalTable: "monitored_containers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_container_visibility_permissions_monitored_containers_ContainerId",
                table: "container_visibility_permissions");

            migrationBuilder.DropIndex(
                name: "IX_monitored_containers_ServerId",
                table: "monitored_containers");

            migrationBuilder.DropIndex(
                name: "IX_container_visibility_permissions_ContainerId",
                table: "container_visibility_permissions");

            migrationBuilder.AlterColumn<Guid>(
                name: "ServerId",
                table: "monitored_containers",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_monitored_containers_server_id_id",
                table: "monitored_containers",
                columns: new[] { "ServerId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_container_visibility_permissions_ServerId_ContainerId",
                table: "container_visibility_permissions",
                columns: new[] { "ServerId", "ContainerId" });

            migrationBuilder.AddForeignKey(
                name: "FK_container_visibility_permissions_monitored_containers_ServerId_ContainerId",
                table: "container_visibility_permissions",
                columns: new[] { "ServerId", "ContainerId" },
                principalTable: "monitored_containers",
                principalColumns: new[] { "ServerId", "Id" },
                onDelete: ReferentialAction.Cascade);
        }
    }
}
