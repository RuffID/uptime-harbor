using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UptimeHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOnboardingInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "onboarding_invitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", maxLength: 262144, nullable: false),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LogIndex = table.Column<long>(type: "bigint", nullable: false),
                    Term = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_onboarding_invitations", x => x.Id);
                    table.CheckConstraint("ck_onboarding_invitations_position", "[ClusterGeneration] > 0 AND [LogIndex] > 0 AND [Term] > 0");
                    table.CheckConstraint("ck_onboarding_invitations_status", "[Status] IN ('Unused','Reserved','Consumed','Revoked','Expired')");
                    table.CheckConstraint("ck_onboarding_invitations_version", "[Version] = 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_onboarding_invitations_ClusterId_ClusterGeneration_LogIndex",
                table: "onboarding_invitations",
                columns: new[] { "ClusterId", "ClusterGeneration", "LogIndex" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "onboarding_invitations");
        }
    }
}
