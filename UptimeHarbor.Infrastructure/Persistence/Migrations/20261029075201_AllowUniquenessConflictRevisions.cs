using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UptimeHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowUniquenessConflictRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_shared_conflicts_revisions",
                table: "shared_conflicts");

            migrationBuilder.AddCheckConstraint(
                name: "ck_shared_conflicts_revisions",
                table: "shared_conflicts",
                sql: "[BaseRevision] >= 0 AND [FirstAcceptedRevision] > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_shared_conflicts_revisions",
                table: "shared_conflicts");

            migrationBuilder.AddCheckConstraint(
                name: "ck_shared_conflicts_revisions",
                table: "shared_conflicts",
                sql: "[BaseRevision] >= 0 AND [FirstAcceptedRevision] > [BaseRevision]");
        }
    }
}
