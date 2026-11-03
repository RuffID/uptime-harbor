using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UptimeHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEnrollmentTrust : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "enrollment_issuers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StorageGeneration = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Purpose = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    PublicCertificate = table.Column<string>(type: "nvarchar(max)", maxLength: 16384, nullable: false),
                    Fingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    SpkiDigest = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    NotBeforeUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    NotAfterUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RetiredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LogIndex = table.Column<long>(type: "bigint", nullable: false),
                    Term = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrollment_issuers", x => x.Id);
                    table.CheckConstraint("ck_enrollment_issuers_lifetime", "[NotAfterUtc] > [NotBeforeUtc] AND [NotAfterUtc] <= DATEADD(year, 3, [NotBeforeUtc])");
                    table.CheckConstraint("ck_enrollment_issuers_position", "[ClusterGeneration] > 0 AND [LogIndex] > 0 AND [Term] > 0");
                    table.CheckConstraint("ck_enrollment_issuers_purpose", "[Purpose] = 'UptimeHarbor.EnrollmentPeer.v1'");
                    table.CheckConstraint("ck_enrollment_issuers_transitions", "([RetiredAtUtc] IS NULL OR [RetiredAtUtc] >= [NotBeforeUtc]) AND ([RevokedAtUtc] IS NULL OR [RevokedAtUtc] >= [NotBeforeUtc])");
                });

            migrationBuilder.CreateTable(
                name: "local_enrollment_issuers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstallIdentity = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    NodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StorageGeneration = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KeyReference = table.Column<string>(type: "varchar(97)", unicode: false, maxLength: 97, nullable: false),
                    CertificateDer = table.Column<byte[]>(type: "varbinary(max)", maxLength: 65536, nullable: false),
                    CertificateDigest = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    SpkiDigest = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_enrollment_issuers", x => x.Id);
                    table.CheckConstraint("ck_local_enrollment_issuers_der", "DATALENGTH([CertificateDer]) BETWEEN 1 AND 65536");
                    table.CheckConstraint("ck_local_enrollment_issuers_generation", "[ClusterGeneration] > 0");
                });

            migrationBuilder.CreateTable(
                name: "local_enrollment_issuances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IssuerEpoch = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestDigest = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    TemplateDigest = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    GenesisId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AuthorizationIndex = table.Column<long>(type: "bigint", nullable: false),
                    CertificateDer = table.Column<byte[]>(type: "varbinary(max)", maxLength: 65536, nullable: false),
                    CertificateDigest = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_enrollment_issuances", x => x.Id);
                    table.CheckConstraint("ck_local_enrollment_issuances_authorization", "([GenesisId] IS NULL AND [AuthorizationIndex] > 0) OR ([GenesisId] IS NOT NULL AND [GenesisId] <> '00000000-0000-0000-0000-000000000000' AND [AuthorizationIndex] = 0)");
                    table.CheckConstraint("ck_local_enrollment_issuances_der", "DATALENGTH([CertificateDer]) BETWEEN 1 AND 65536");
                    table.ForeignKey(
                        name: "FK_local_enrollment_issuances_local_enrollment_issuers_IssuerEpoch",
                        column: x => x.IssuerEpoch,
                        principalTable: "local_enrollment_issuers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_enrollment_issuers_ClusterId_ClusterGeneration_LogIndex",
                table: "enrollment_issuers",
                columns: new[] { "ClusterId", "ClusterGeneration", "LogIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_enrollment_issuers_Fingerprint",
                table: "enrollment_issuers",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_enrollment_issuers_NodeId_StorageGeneration",
                table: "enrollment_issuers",
                columns: new[] { "NodeId", "StorageGeneration" });

            migrationBuilder.CreateIndex(
                name: "IX_enrollment_issuers_SpkiDigest",
                table: "enrollment_issuers",
                column: "SpkiDigest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_local_enrollment_issuances_AttemptId",
                table: "local_enrollment_issuances",
                column: "AttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_local_enrollment_issuances_CertificateDigest",
                table: "local_enrollment_issuances",
                column: "CertificateDigest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_local_enrollment_issuances_IssuerEpoch",
                table: "local_enrollment_issuances",
                column: "IssuerEpoch");

            migrationBuilder.CreateIndex(
                name: "IX_local_enrollment_issuers_CertificateDigest",
                table: "local_enrollment_issuers",
                column: "CertificateDigest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_local_enrollment_issuers_ClusterId_ClusterGeneration",
                table: "local_enrollment_issuers",
                columns: new[] { "ClusterId", "ClusterGeneration" });

            migrationBuilder.CreateIndex(
                name: "IX_local_enrollment_issuers_KeyReference",
                table: "local_enrollment_issuers",
                column: "KeyReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_local_enrollment_issuers_SpkiDigest",
                table: "local_enrollment_issuers",
                column: "SpkiDigest",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "enrollment_issuers");

            migrationBuilder.DropTable(
                name: "local_enrollment_issuances");

            migrationBuilder.DropTable(
                name: "local_enrollment_issuers");
        }
    }
}
