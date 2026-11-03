using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UptimeHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalInstallationSetup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "local_attempt_steps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_attempt_steps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "local_installation_profiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    InstallIdentity = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_installation_profiles", x => x.Id);
                    table.CheckConstraint("ck_local_installation_singleton", "[Id] = 1");
                });

            migrationBuilder.CreateTable(
                name: "local_onboarding_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_onboarding_attempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "local_setup_accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedForInstallIdentity = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NormalizedLogin = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Stamp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Disabled = table.Column<bool>(type: "bit", nullable: false),
                    FailedCount = table.Column<int>(type: "int", nullable: false),
                    LockoutUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_setup_accounts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_local_attempt_steps_AttemptId",
                table: "local_attempt_steps",
                column: "AttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_local_installation_profiles_InstallIdentity",
                table: "local_installation_profiles",
                column: "InstallIdentity",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_local_setup_accounts_CreatedForInstallIdentity",
                table: "local_setup_accounts",
                column: "CreatedForInstallIdentity",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "local_attempt_steps");

            migrationBuilder.DropTable(
                name: "local_installation_profiles");

            migrationBuilder.DropTable(
                name: "local_onboarding_attempts");

            migrationBuilder.DropTable(
                name: "local_setup_accounts");
        }
    }
}
