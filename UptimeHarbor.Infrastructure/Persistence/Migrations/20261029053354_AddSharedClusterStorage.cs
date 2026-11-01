using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UptimeHarbor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSharedClusterStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "container_visibility_permissions");

            migrationBuilder.DropTable(
                name: "server_visibility_permissions");

            migrationBuilder.DropIndex(
                name: "UserNameIndex",
                table: "users");

            migrationBuilder.DropIndex(
                name: "RoleNameIndex",
                table: "roles");

            migrationBuilder.AlterColumn<string>(
                name: "UserName",
                table: "users",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "SecurityStamp",
                table: "users",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "PasswordHash",
                table: "users",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "NormalizedUserName",
                table: "users",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "",
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ConcurrencyStamp",
                table: "users",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAtUtc",
                table: "users",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PasswordHashFormatVersion",
                table: "users",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "UserRevision",
                table: "users",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ConfigRevision",
                table: "telegram_chats",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AlterColumn<string>(
                name: "NormalizedName",
                table: "roles",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "",
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "roles",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BindingGeneration",
                table: "notification_bindings",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<long>(
                name: "BindingRevision",
                table: "notification_bindings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "CheckGeneration",
                table: "monitored_servers",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<long>(
                name: "ConfigRevision",
                table: "monitored_servers",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "CheckGeneration",
                table: "monitored_containers",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<long>(
                name: "ConfigRevision",
                table: "monitored_containers",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "applied_journal_receipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Digest = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LogIndex = table.Column<long>(type: "bigint", nullable: false),
                    Term = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_applied_journal_receipts", x => x.Id);
                    table.CheckConstraint("ck_applied_journal_receipts_position", "[ClusterGeneration] > 0 AND [LogIndex] > 0 AND [Term] > 0");
                });

            migrationBuilder.CreateTable(
                name: "cluster_members",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StorageGeneration = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PeerUri = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    RaftMemberId = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    MembershipVersion = table.Column<long>(type: "bigint", nullable: false),
                    IsVoting = table.Column<bool>(type: "bit", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LogIndex = table.Column<long>(type: "bigint", nullable: false),
                    Term = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cluster_members", x => x.Id);
                    table.CheckConstraint("ck_cluster_members_position", "[ClusterGeneration] > 0 AND [LogIndex] > 0 AND [Term] > 0");
                    table.CheckConstraint("ck_cluster_members_version", "[MembershipVersion] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "cluster_trust_roots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Fingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    PublicCertificate = table.Column<string>(type: "nvarchar(max)", maxLength: 16384, nullable: false),
                    RetiredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LogIndex = table.Column<long>(type: "bigint", nullable: false),
                    Term = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cluster_trust_roots", x => x.Id);
                    table.CheckConstraint("ck_cluster_trust_roots_position", "[ClusterGeneration] > 0 AND [LogIndex] > 0 AND [Term] > 0");
                });

            migrationBuilder.CreateTable(
                name: "local_delivery_results",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TelegramMessageId = table.Column<long>(type: "bigint", nullable: true),
                    FailureKind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_delivery_results", x => x.Id);
                    table.CheckConstraint("ck_local_delivery_results_outcome", "[Outcome] IN ('Succeeded','Failed','CancelledBeforeTransport')");
                });

            migrationBuilder.CreateTable(
                name: "local_node_state",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    StorageGeneration = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionEpoch = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WalStoreIdentity = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SnapshotDigest = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    SnapshotCutIndex = table.Column<long>(type: "bigint", nullable: false),
                    ReceivedIndex = table.Column<long>(type: "bigint", nullable: false),
                    CommittedIndex = table.Column<long>(type: "bigint", nullable: false),
                    MinReplayIndex = table.Column<long>(type: "bigint", nullable: false),
                    IsQuarantined = table.Column<bool>(type: "bit", nullable: false),
                    IsMaintenanceDisabled = table.Column<bool>(type: "bit", nullable: false),
                    RecoveryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_node_state", x => x.Id);
                    table.CheckConstraint("ck_local_node_state_indices", "[ClusterGeneration] > 0 AND [SnapshotCutIndex] >= 0 AND [ReceivedIndex] >= 0 AND [CommittedIndex] >= 0 AND [MinReplayIndex] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "local_observation_cursors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObserverNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StorageGeneration = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CheckGeneration = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LastSequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_observation_cursors", x => x.Id);
                    table.CheckConstraint("ck_local_observation_cursors_sequence", "[LastSequence] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "local_pending_operations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SystemSource = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BaseRevision = table.Column<long>(type: "bigint", nullable: false),
                    ActorUserRevision = table.Column<long>(type: "bigint", nullable: false),
                    Digest = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    ProtectedPayload = table.Column<string>(type: "nvarchar(max)", maxLength: 262144, nullable: false),
                    PayloadVersion = table.Column<int>(type: "int", nullable: false),
                    ProtocolVersion = table.Column<int>(type: "int", nullable: false),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SubmissionAttempts = table.Column<int>(type: "int", nullable: false),
                    RequestedControlId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RequestedControlKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    ReplacementOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_pending_operations", x => x.Id);
                    table.CheckConstraint("ck_local_pending_operations_actor", "([ActorUserId] IS NOT NULL AND [SystemSource] IS NULL) OR ([ActorUserId] IS NULL AND [SystemSource] IS NOT NULL)");
                    table.CheckConstraint("ck_local_pending_operations_state", "[State] IN ('LocalPending','AwaitingDecision','NeedsUserConfirmation','Resolved')");
                    table.CheckConstraint("ck_local_pending_operations_versions", "[ClusterGeneration] > 0 AND [BaseRevision] >= 0 AND [ActorUserRevision] >= 0 AND [SubmissionAttempts] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "membership_changes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpectedVersion = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LogIndex = table.Column<long>(type: "bigint", nullable: false),
                    Term = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_membership_changes", x => x.Id);
                    table.CheckConstraint("ck_membership_changes_position", "[ClusterGeneration] > 0 AND [LogIndex] > 0 AND [Term] > 0");
                    table.CheckConstraint("ck_membership_changes_state", "[State] IN ('Requested','CatchingUp','ProtocolCommitted','Activated','Removed') AND [ExpectedVersion] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "observation_frontiers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObserverNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StorageGeneration = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CheckGeneration = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AcceptedSequence = table.Column<long>(type: "bigint", nullable: false),
                    CheckedWatermarkUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsHistoricalAssemblyOpen = table.Column<bool>(type: "bit", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LogIndex = table.Column<long>(type: "bigint", nullable: false),
                    Term = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_observation_frontiers", x => x.Id);
                    table.CheckConstraint("ck_observation_frontiers_position", "[ClusterGeneration] > 0 AND [LogIndex] > 0 AND [Term] > 0");
                    table.CheckConstraint("ck_observation_frontiers_sequence", "[AcceptedSequence] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "observer_results",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObserverNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StorageGeneration = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CheckGeneration = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfigRevision = table.Column<long>(type: "bigint", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    CheckedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DurationMilliseconds = table.Column<long>(type: "bigint", nullable: false),
                    Succeeded = table.Column<bool>(type: "bit", nullable: false),
                    FailureKind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    EndpointName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    HistoricalOnly = table.Column<bool>(type: "bit", nullable: false),
                    Digest = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LogIndex = table.Column<long>(type: "bigint", nullable: false),
                    Term = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_observer_results", x => x.Id);
                    table.CheckConstraint("ck_observer_results_position", "[ClusterGeneration] > 0 AND [LogIndex] > 0 AND [Term] > 0");
                    table.CheckConstraint("ck_observer_results_values", "[ConfigRevision] > 0 AND [Sequence] > 0 AND [ConsecutiveFailures] >= 0 AND [DurationMilliseconds] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "operation_controls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetDigest = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    ExpectedHead = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ReplacementOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LogIndex = table.Column<long>(type: "bigint", nullable: false),
                    Term = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operation_controls", x => x.Id);
                    table.CheckConstraint("ck_operation_controls_position", "[ClusterGeneration] > 0 AND [LogIndex] > 0 AND [Term] > 0");
                    table.CheckConstraint("ck_operation_controls_replacement", "([Kind] = 'Replace' AND [ReplacementOperationId] IS NOT NULL) OR ([Kind] = 'Cancel' AND [ReplacementOperationId] IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "operation_decisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Digest = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    AppliedRevision = table.Column<long>(type: "bigint", nullable: true),
                    ReasonCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ConflictId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReplacedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LogIndex = table.Column<long>(type: "bigint", nullable: false),
                    Term = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operation_decisions", x => x.Id);
                    table.CheckConstraint("ck_operation_decisions_conflict", "([Outcome] = 'Conflict' AND [ConflictId] IS NOT NULL) OR ([Outcome] <> 'Conflict' AND [ConflictId] IS NULL)");
                    table.CheckConstraint("ck_operation_decisions_outcome", "[Outcome] IN ('Committed','Conflict','Rejected','Cancelled','Replaced')");
                    table.CheckConstraint("ck_operation_decisions_position", "[ClusterGeneration] > 0 AND [LogIndex] > 0 AND [Term] > 0");
                    table.CheckConstraint("ck_operation_decisions_replacement", "([Outcome] = 'Replaced' AND [ReplacedBy] IS NOT NULL) OR ([Outcome] <> 'Replaced' AND [ReplacedBy] IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "peer_trust_credentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Fingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    PublicCertificate = table.Column<string>(type: "nvarchar(max)", maxLength: 16384, nullable: false),
                    NotAfterUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Purpose = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AdmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AdmissionExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AdmissionConsumedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LogIndex = table.Column<long>(type: "bigint", nullable: false),
                    Term = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_peer_trust_credentials", x => x.Id);
                    table.CheckConstraint("ck_peer_trust_credentials_position", "[ClusterGeneration] > 0 AND [LogIndex] > 0 AND [Term] > 0");
                    table.CheckConstraint("ck_peer_trust_credentials_purpose", "[Purpose] IN ('Admission','Active','Retired')");
                });

            migrationBuilder.CreateTable(
                name: "projection_cursors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LogIndex = table.Column<long>(type: "bigint", nullable: false),
                    Term = table.Column<long>(type: "bigint", nullable: false),
                    Digest = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    InstalledSnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projection_cursors", x => x.Id);
                    table.CheckConstraint("ck_projection_cursors_position", "[ClusterGeneration] > 0 AND [LogIndex] >= 0 AND [Term] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "recovery_markers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BaselineSnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BaselineDigest = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    PreviousClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LossAcknowledged = table.Column<bool>(type: "bit", nullable: false),
                    FencingConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    MaintenanceDisabled = table.Column<bool>(type: "bit", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LogIndex = table.Column<long>(type: "bigint", nullable: false),
                    Term = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recovery_markers", x => x.Id);
                    table.CheckConstraint("ck_recovery_markers_position", "[ClusterGeneration] > 0 AND [LogIndex] > 0 AND [Term] > 0");
                });

            migrationBuilder.CreateTable(
                name: "shared_conflicts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BaseRevision = table.Column<long>(type: "bigint", nullable: false),
                    FirstAcceptedRevision = table.Column<long>(type: "bigint", nullable: false),
                    ProtectedBasePayload = table.Column<string>(type: "nvarchar(max)", maxLength: 262144, nullable: false),
                    ProtectedAcceptedPayload = table.Column<string>(type: "nvarchar(max)", maxLength: 262144, nullable: false),
                    ProtectedProposedPayload = table.Column<string>(type: "nvarchar(max)", maxLength: 262144, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolutionOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LogIndex = table.Column<long>(type: "bigint", nullable: false),
                    Term = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_conflicts", x => x.Id);
                    table.CheckConstraint("ck_shared_conflicts_position", "[ClusterGeneration] > 0 AND [LogIndex] > 0 AND [Term] > 0");
                    table.CheckConstraint("ck_shared_conflicts_revisions", "[BaseRevision] >= 0 AND [FirstAcceptedRevision] > [BaseRevision]");
                });

            migrationBuilder.CreateTable(
                name: "shared_incidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CheckGeneration = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    TransitionKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OpenedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Completion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LogIndex = table.Column<long>(type: "bigint", nullable: false),
                    Term = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_incidents", x => x.Id);
                    table.CheckConstraint("ck_shared_incidents_completion", "([CompletedAtUtc] IS NULL AND [Completion] IS NULL) OR ([CompletedAtUtc] IS NOT NULL AND [Completion] IS NOT NULL AND [CompletedAtUtc] >= [OpenedAtUtc] AND [Completion] IN ('Recovered','SupersededConfiguration','Deleted'))");
                    table.CheckConstraint("ck_shared_incidents_kind", "[Kind] IN ('Target','Coverage','Node')");
                    table.CheckConstraint("ck_shared_incidents_position", "[ClusterGeneration] > 0 AND [LogIndex] > 0 AND [Term] > 0");
                });

            migrationBuilder.CreateTable(
                name: "shared_tombstones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LogIndex = table.Column<long>(type: "bigint", nullable: false),
                    Term = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_tombstones", x => x.Id);
                    table.CheckConstraint("ck_shared_tombstones_kind", "[Kind] IN ('Server','Container','Chat','Binding','Account','Node','ObserverAssignment','NodeChatRoute')");
                    table.CheckConstraint("ck_shared_tombstones_position", "[ClusterGeneration] > 0 AND [LogIndex] > 0 AND [Term] > 0");
                });

            migrationBuilder.CreateTable(
                name: "target_observation_states",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CheckGeneration = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Availability = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Coverage = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    EvaluationTimeUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FrontierDigest = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    ProblemDirectionsJson = table.Column<string>(type: "nvarchar(max)", maxLength: 262144, nullable: false),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LogIndex = table.Column<long>(type: "bigint", nullable: false),
                    Term = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_target_observation_states", x => x.Id);
                    table.CheckConstraint("ck_target_observation_states_availability", "[Availability] IN ('Unknown','Available','Partial','Unavailable')");
                    table.CheckConstraint("ck_target_observation_states_coverage", "[Coverage] IN ('Initializing','Complete','Degraded','Lost')");
                    table.CheckConstraint("ck_target_observation_states_position", "[ClusterGeneration] > 0 AND [LogIndex] > 0 AND [Term] > 0");
                });

            migrationBuilder.CreateTable(
                name: "node_chat_routes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChatId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LogIndex = table.Column<long>(type: "bigint", nullable: false),
                    Term = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_node_chat_routes", x => x.Id);
                    table.CheckConstraint("ck_node_chat_routes_position", "[ClusterGeneration] > 0 AND [LogIndex] > 0 AND [Term] > 0");
                    table.ForeignKey(
                        name: "FK_node_chat_routes_cluster_members_NodeId",
                        column: x => x.NodeId,
                        principalTable: "cluster_members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_node_chat_routes_telegram_chats_ChatId",
                        column: x => x.ChatId,
                        principalTable: "telegram_chats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "observer_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObserverNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CheckGeneration = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfigRevision = table.Column<long>(type: "bigint", nullable: false),
                    AssignedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LogIndex = table.Column<long>(type: "bigint", nullable: false),
                    Term = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_observer_assignments", x => x.Id);
                    table.CheckConstraint("ck_observer_assignments_position", "[ClusterGeneration] > 0 AND [LogIndex] > 0 AND [Term] > 0");
                    table.CheckConstraint("ck_observer_assignments_revision", "[ConfigRevision] > 0");
                    table.ForeignKey(
                        name: "FK_observer_assignments_cluster_members_ObserverNodeId",
                        column: x => x.ObserverNodeId,
                        principalTable: "cluster_members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "local_captured_observations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StreamId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Digest = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", maxLength: 262144, nullable: false),
                    CheckedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AcceptedSequence = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_captured_observations", x => x.Id);
                    table.CheckConstraint("ck_local_captured_observations_sequence", "[Sequence] > 0");
                    table.ForeignKey(
                        name: "FK_local_captured_observations_local_observation_cursors_StreamId",
                        column: x => x.StreamId,
                        principalTable: "local_observation_cursors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "incident_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FrontierDigest = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    ProtectedPayload = table.Column<string>(type: "nvarchar(max)", maxLength: 262144, nullable: false),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LogIndex = table.Column<long>(type: "bigint", nullable: false),
                    Term = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incident_events", x => x.Id);
                    table.UniqueConstraint("AK_incident_events_Id_IncidentId", x => new { x.Id, x.IncidentId });
                    table.CheckConstraint("ck_incident_events_position", "[ClusterGeneration] > 0 AND [LogIndex] > 0 AND [Term] > 0");
                    table.CheckConstraint("ck_incident_events_sequence", "[Sequence] > 0");
                    table.ForeignKey(
                        name: "FK_incident_events_shared_incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "shared_incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "delivery_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IntentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    SenderNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SenderEpoch = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TelegramMessageId = table.Column<long>(type: "bigint", nullable: true),
                    FailureKind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LogIndex = table.Column<long>(type: "bigint", nullable: false),
                    Term = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_attempts", x => x.Id);
                    table.CheckConstraint("ck_delivery_attempts_number", "[Number] BETWEEN 1 AND 5");
                    table.CheckConstraint("ck_delivery_attempts_outcome", "[Outcome] IN ('Unknown','Succeeded','Failed','CancelledBeforeTransport')");
                    table.CheckConstraint("ck_delivery_attempts_position", "[ClusterGeneration] > 0 AND [LogIndex] > 0 AND [Term] > 0");
                });

            migrationBuilder.CreateTable(
                name: "delivery_intents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChatId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    BindingGeneration = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LogicalKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ProtectedPayload = table.Column<string>(type: "nvarchar(max)", maxLength: 262144, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    ActiveAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SummaryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LogIndex = table.Column<long>(type: "bigint", nullable: false),
                    Term = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_intents", x => x.Id);
                    table.CheckConstraint("ck_delivery_intents_actor", "([Kind] = 'Test' AND [ActorUserId] IS NOT NULL) OR ([Kind] <> 'Test' AND [ActorUserId] IS NULL)");
                    table.CheckConstraint("ck_delivery_intents_attempts", "[AttemptCount] BETWEEN 0 AND 5");
                    table.CheckConstraint("ck_delivery_intents_event", "([Kind] IN ('Test','Summary') AND [IncidentId] IS NULL AND [EventId] IS NULL) OR ([Kind] IN ('Problem','Recovery','Update') AND [IncidentId] IS NOT NULL AND [EventId] IS NOT NULL)");
                    table.CheckConstraint("ck_delivery_intents_kind", "[Kind] IN ('Problem','Recovery','Update','Summary','Test')");
                    table.CheckConstraint("ck_delivery_intents_position", "[ClusterGeneration] > 0 AND [LogIndex] > 0 AND [Term] > 0");
                    table.CheckConstraint("ck_delivery_intents_state", "[State] IN ('Planned','AttemptStarted','Succeeded','RetryDue','Failed','Cancelled','SuppressedBySummary')");
                    table.ForeignKey(
                        name: "FK_delivery_intents_incident_events_EventId_IncidentId",
                        columns: x => new { x.EventId, x.IncidentId },
                        principalTable: "incident_events",
                        principalColumns: new[] { "Id", "IncidentId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "delivery_summaries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChatId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PartNumber = table.Column<int>(type: "int", nullable: false),
                    CutIndex = table.Column<long>(type: "bigint", nullable: false),
                    IntentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(3500)", maxLength: 3500, nullable: false),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LogIndex = table.Column<long>(type: "bigint", nullable: false),
                    Term = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_summaries", x => x.Id);
                    table.CheckConstraint("ck_delivery_summaries_part", "[PartNumber] > 0 AND [CutIndex] > 0 AND [CutIndex] <= [LogIndex]");
                    table.CheckConstraint("ck_delivery_summaries_position", "[ClusterGeneration] > 0 AND [LogIndex] > 0 AND [Term] > 0");
                    table.ForeignKey(
                        name: "FK_delivery_summaries_delivery_intents_IntentId",
                        column: x => x.IntentId,
                        principalTable: "delivery_intents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "recovery_entitlements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChatId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProblemIntentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecoveryIntentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConsumedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LogIndex = table.Column<long>(type: "bigint", nullable: false),
                    Term = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recovery_entitlements", x => x.Id);
                    table.CheckConstraint("ck_recovery_entitlements_position", "[ClusterGeneration] > 0 AND [LogIndex] > 0 AND [Term] > 0");
                    table.ForeignKey(
                        name: "FK_recovery_entitlements_delivery_intents_ProblemIntentId",
                        column: x => x.ProblemIntentId,
                        principalTable: "delivery_intents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recovery_entitlements_delivery_intents_RecoveryIntentId",
                        column: x => x.RecoveryIntentId,
                        principalTable: "delivery_intents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recovery_entitlements_shared_incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "shared_incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "summary_sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SummaryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceIntentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClusterGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LogIndex = table.Column<long>(type: "bigint", nullable: false),
                    Term = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_summary_sources", x => x.Id);
                    table.CheckConstraint("ck_summary_sources_position", "[ClusterGeneration] > 0 AND [LogIndex] > 0 AND [Term] > 0");
                    table.ForeignKey(
                        name: "FK_summary_sources_delivery_intents_SourceIntentId",
                        column: x => x.SourceIntentId,
                        principalTable: "delivery_intents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_summary_sources_delivery_summaries_SummaryId",
                        column: x => x.SummaryId,
                        principalTable: "delivery_summaries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_summary_sources_incident_events_SourceEventId",
                        column: x => x.SourceEventId,
                        principalTable: "incident_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "users",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_deleted",
                table: "users",
                sql: "[DeletedAtUtc] IS NULL OR [IsEnabled] = 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_revision",
                table: "users",
                sql: "[UserRevision] >= 0 AND [PasswordHashFormatVersion] = 1");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "roles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_roles_administrator",
                table: "roles",
                sql: "[Name] = 'Administrator' AND [NormalizedName] = 'ADMINISTRATOR'");

            migrationBuilder.CreateIndex(
                name: "IX_applied_journal_receipts_ClusterId_ClusterGeneration_LogIndex",
                table: "applied_journal_receipts",
                columns: new[] { "ClusterId", "ClusterGeneration", "LogIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cluster_members_ClusterId_ClusterGeneration_LogIndex",
                table: "cluster_members",
                columns: new[] { "ClusterId", "ClusterGeneration", "LogIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_cluster_members_RaftMemberId",
                table: "cluster_members",
                column: "RaftMemberId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cluster_trust_roots_ClusterId_ClusterGeneration_LogIndex",
                table: "cluster_trust_roots",
                columns: new[] { "ClusterId", "ClusterGeneration", "LogIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_cluster_trust_roots_Fingerprint",
                table: "cluster_trust_roots",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_delivery_attempts_ClusterId_ClusterGeneration_LogIndex",
                table: "delivery_attempts",
                columns: new[] { "ClusterId", "ClusterGeneration", "LogIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_attempts_IntentId_Number",
                table: "delivery_attempts",
                columns: new[] { "IntentId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_delivery_intents_ClusterId_ClusterGeneration_LogIndex",
                table: "delivery_intents",
                columns: new[] { "ClusterId", "ClusterGeneration", "LogIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_intents_EventId_IncidentId",
                table: "delivery_intents",
                columns: new[] { "EventId", "IncidentId" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_intents_EventId_Kind_ChatId",
                table: "delivery_intents",
                columns: new[] { "EventId", "Kind", "ChatId" },
                unique: true,
                filter: "[EventId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_intents_IncidentId_Kind_ChatId",
                table: "delivery_intents",
                columns: new[] { "IncidentId", "Kind", "ChatId" },
                unique: true,
                filter: "[IncidentId] IS NOT NULL AND [Kind] IN ('Problem','Recovery')");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_intents_LogicalKey",
                table: "delivery_intents",
                column: "LogicalKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_delivery_intents_State_NextAttemptAtUtc",
                table: "delivery_intents",
                columns: new[] { "State", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_intents_SummaryId",
                table: "delivery_intents",
                column: "SummaryId");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_summaries_ClusterId_ClusterGeneration_LogIndex",
                table: "delivery_summaries",
                columns: new[] { "ClusterId", "ClusterGeneration", "LogIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_summaries_IntentId",
                table: "delivery_summaries",
                column: "IntentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_delivery_summaries_PlanId_ChatId_PartNumber",
                table: "delivery_summaries",
                columns: new[] { "PlanId", "ChatId", "PartNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_incident_events_ClusterId_ClusterGeneration_LogIndex",
                table: "incident_events",
                columns: new[] { "ClusterId", "ClusterGeneration", "LogIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_incident_events_IncidentId_Sequence",
                table: "incident_events",
                columns: new[] { "IncidentId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_local_captured_observations_StreamId_Sequence",
                table: "local_captured_observations",
                columns: new[] { "StreamId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_local_observation_cursors_TargetId_ObserverNodeId_StorageGeneration_CheckGeneration",
                table: "local_observation_cursors",
                columns: new[] { "TargetId", "ObserverNodeId", "StorageGeneration", "CheckGeneration" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_local_pending_operations_State_NextAttemptAtUtc",
                table: "local_pending_operations",
                columns: new[] { "State", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_membership_changes_ClusterId_ClusterGeneration",
                table: "membership_changes",
                columns: new[] { "ClusterId", "ClusterGeneration" },
                unique: true,
                filter: "[State] IN ('Requested','CatchingUp','ProtocolCommitted')");

            migrationBuilder.CreateIndex(
                name: "IX_membership_changes_ClusterId_ClusterGeneration_LogIndex",
                table: "membership_changes",
                columns: new[] { "ClusterId", "ClusterGeneration", "LogIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_node_chat_routes_ChatId",
                table: "node_chat_routes",
                column: "ChatId");

            migrationBuilder.CreateIndex(
                name: "IX_node_chat_routes_ClusterId_ClusterGeneration_LogIndex",
                table: "node_chat_routes",
                columns: new[] { "ClusterId", "ClusterGeneration", "LogIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_node_chat_routes_NodeId_ChatId",
                table: "node_chat_routes",
                columns: new[] { "NodeId", "ChatId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_observation_frontiers_ClusterId_ClusterGeneration_LogIndex",
                table: "observation_frontiers",
                columns: new[] { "ClusterId", "ClusterGeneration", "LogIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_observation_frontiers_TargetId_ObserverNodeId_StorageGeneration_CheckGeneration",
                table: "observation_frontiers",
                columns: new[] { "TargetId", "ObserverNodeId", "StorageGeneration", "CheckGeneration" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_observer_assignments_ClusterId_ClusterGeneration_LogIndex",
                table: "observer_assignments",
                columns: new[] { "ClusterId", "ClusterGeneration", "LogIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_observer_assignments_ObserverNodeId",
                table: "observer_assignments",
                column: "ObserverNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_observer_assignments_TargetId_ObserverNodeId",
                table: "observer_assignments",
                columns: new[] { "TargetId", "ObserverNodeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_observer_results_ClusterId_ClusterGeneration_LogIndex",
                table: "observer_results",
                columns: new[] { "ClusterId", "ClusterGeneration", "LogIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_observer_results_TargetId_CheckGeneration_CheckedAtUtc",
                table: "observer_results",
                columns: new[] { "TargetId", "CheckGeneration", "CheckedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_observer_results_TargetId_ObserverNodeId_StorageGeneration_CheckGeneration_Sequence",
                table: "observer_results",
                columns: new[] { "TargetId", "ObserverNodeId", "StorageGeneration", "CheckGeneration", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_operation_controls_ClusterId_ClusterGeneration_LogIndex",
                table: "operation_controls",
                columns: new[] { "ClusterId", "ClusterGeneration", "LogIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_operation_controls_TargetOperationId",
                table: "operation_controls",
                column: "TargetOperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_operation_decisions_ClusterId_ClusterGeneration_LogIndex",
                table: "operation_decisions",
                columns: new[] { "ClusterId", "ClusterGeneration", "LogIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_peer_trust_credentials_AdmissionId",
                table: "peer_trust_credentials",
                column: "AdmissionId",
                unique: true,
                filter: "[AdmissionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_peer_trust_credentials_ClusterId_ClusterGeneration_LogIndex",
                table: "peer_trust_credentials",
                columns: new[] { "ClusterId", "ClusterGeneration", "LogIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_peer_trust_credentials_Fingerprint",
                table: "peer_trust_credentials",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_peer_trust_credentials_NodeId",
                table: "peer_trust_credentials",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_recovery_entitlements_ClusterId_ClusterGeneration_LogIndex",
                table: "recovery_entitlements",
                columns: new[] { "ClusterId", "ClusterGeneration", "LogIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_recovery_entitlements_IncidentId_ChatId",
                table: "recovery_entitlements",
                columns: new[] { "IncidentId", "ChatId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recovery_entitlements_ProblemIntentId",
                table: "recovery_entitlements",
                column: "ProblemIntentId");

            migrationBuilder.CreateIndex(
                name: "IX_recovery_entitlements_RecoveryIntentId",
                table: "recovery_entitlements",
                column: "RecoveryIntentId");

            migrationBuilder.CreateIndex(
                name: "IX_recovery_markers_ClusterId_ClusterGeneration_LogIndex",
                table: "recovery_markers",
                columns: new[] { "ClusterId", "ClusterGeneration", "LogIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_shared_conflicts_ClusterId_ClusterGeneration_LogIndex",
                table: "shared_conflicts",
                columns: new[] { "ClusterId", "ClusterGeneration", "LogIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_shared_conflicts_OperationId",
                table: "shared_conflicts",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shared_conflicts_ResolvedAtUtc",
                table: "shared_conflicts",
                column: "ResolvedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_shared_incidents_ClusterId_ClusterGeneration_LogIndex",
                table: "shared_incidents",
                columns: new[] { "ClusterId", "ClusterGeneration", "LogIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_shared_incidents_CompletedAtUtc",
                table: "shared_incidents",
                column: "CompletedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_shared_incidents_TargetId_CheckGeneration_Kind_TransitionKey",
                table: "shared_incidents",
                columns: new[] { "TargetId", "CheckGeneration", "Kind", "TransitionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shared_tombstones_ClusterId_ClusterGeneration_LogIndex",
                table: "shared_tombstones",
                columns: new[] { "ClusterId", "ClusterGeneration", "LogIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_summary_sources_ClusterId_ClusterGeneration_LogIndex",
                table: "summary_sources",
                columns: new[] { "ClusterId", "ClusterGeneration", "LogIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_summary_sources_SourceEventId",
                table: "summary_sources",
                column: "SourceEventId");

            migrationBuilder.CreateIndex(
                name: "IX_summary_sources_SourceIntentId",
                table: "summary_sources",
                column: "SourceIntentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_summary_sources_SummaryId_SourceEventId_SourceIntentId",
                table: "summary_sources",
                columns: new[] { "SummaryId", "SourceEventId", "SourceIntentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_target_observation_states_ClusterId_ClusterGeneration_LogIndex",
                table: "target_observation_states",
                columns: new[] { "ClusterId", "ClusterGeneration", "LogIndex" });

            migrationBuilder.AddForeignKey(
                name: "FK_delivery_attempts_delivery_intents_IntentId",
                table: "delivery_attempts",
                column: "IntentId",
                principalTable: "delivery_intents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_delivery_intents_delivery_summaries_SummaryId",
                table: "delivery_intents",
                column: "SummaryId",
                principalTable: "delivery_summaries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_delivery_summaries_delivery_intents_IntentId",
                table: "delivery_summaries");

            migrationBuilder.DropTable(
                name: "applied_journal_receipts");

            migrationBuilder.DropTable(
                name: "cluster_trust_roots");

            migrationBuilder.DropTable(
                name: "delivery_attempts");

            migrationBuilder.DropTable(
                name: "local_captured_observations");

            migrationBuilder.DropTable(
                name: "local_delivery_results");

            migrationBuilder.DropTable(
                name: "local_node_state");

            migrationBuilder.DropTable(
                name: "local_pending_operations");

            migrationBuilder.DropTable(
                name: "membership_changes");

            migrationBuilder.DropTable(
                name: "node_chat_routes");

            migrationBuilder.DropTable(
                name: "observation_frontiers");

            migrationBuilder.DropTable(
                name: "observer_assignments");

            migrationBuilder.DropTable(
                name: "observer_results");

            migrationBuilder.DropTable(
                name: "operation_controls");

            migrationBuilder.DropTable(
                name: "operation_decisions");

            migrationBuilder.DropTable(
                name: "peer_trust_credentials");

            migrationBuilder.DropTable(
                name: "projection_cursors");

            migrationBuilder.DropTable(
                name: "recovery_entitlements");

            migrationBuilder.DropTable(
                name: "recovery_markers");

            migrationBuilder.DropTable(
                name: "shared_conflicts");

            migrationBuilder.DropTable(
                name: "shared_tombstones");

            migrationBuilder.DropTable(
                name: "summary_sources");

            migrationBuilder.DropTable(
                name: "target_observation_states");

            migrationBuilder.DropTable(
                name: "local_observation_cursors");

            migrationBuilder.DropTable(
                name: "cluster_members");

            migrationBuilder.DropTable(
                name: "delivery_intents");

            migrationBuilder.DropTable(
                name: "delivery_summaries");

            migrationBuilder.DropTable(
                name: "incident_events");

            migrationBuilder.DropTable(
                name: "shared_incidents");

            migrationBuilder.DropIndex(
                name: "UserNameIndex",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_deleted",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_revision",
                table: "users");

            migrationBuilder.DropIndex(
                name: "RoleNameIndex",
                table: "roles");

            migrationBuilder.DropCheckConstraint(
                name: "ck_roles_administrator",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "users");

            migrationBuilder.DropColumn(
                name: "PasswordHashFormatVersion",
                table: "users");

            migrationBuilder.DropColumn(
                name: "UserRevision",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ConfigRevision",
                table: "telegram_chats");

            migrationBuilder.DropColumn(
                name: "BindingGeneration",
                table: "notification_bindings");

            migrationBuilder.DropColumn(
                name: "BindingRevision",
                table: "notification_bindings");

            migrationBuilder.DropColumn(
                name: "CheckGeneration",
                table: "monitored_servers");

            migrationBuilder.DropColumn(
                name: "ConfigRevision",
                table: "monitored_servers");

            migrationBuilder.DropColumn(
                name: "CheckGeneration",
                table: "monitored_containers");

            migrationBuilder.DropColumn(
                name: "ConfigRevision",
                table: "monitored_containers");

            migrationBuilder.AlterColumn<string>(
                name: "UserName",
                table: "users",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256);

            migrationBuilder.AlterColumn<string>(
                name: "SecurityStamp",
                table: "users",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "PasswordHash",
                table: "users",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(2048)",
                oldMaxLength: 2048,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "NormalizedUserName",
                table: "users",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.AlterColumn<string>(
                name: "ConcurrencyStamp",
                table: "users",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "NormalizedName",
                table: "roles",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "roles",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256);

            migrationBuilder.CreateTable(
                name: "server_visibility_permissions",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_server_visibility_permissions", x => new { x.UserId, x.ServerId });
                    table.ForeignKey(
                        name: "FK_server_visibility_permissions_monitored_servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "monitored_servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_server_visibility_permissions_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "container_visibility_permissions",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContainerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_container_visibility_permissions", x => new { x.UserId, x.ContainerId });
                    table.ForeignKey(
                        name: "FK_container_visibility_permissions_monitored_containers_ContainerId",
                        column: x => x.ContainerId,
                        principalTable: "monitored_containers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_container_visibility_permissions_server_visibility_permissions_UserId_ServerId",
                        columns: x => new { x.UserId, x.ServerId },
                        principalTable: "server_visibility_permissions",
                        principalColumns: new[] { "UserId", "ServerId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "users",
                column: "NormalizedUserName",
                unique: true,
                filter: "[NormalizedUserName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "roles",
                column: "NormalizedName",
                unique: true,
                filter: "[NormalizedName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_container_visibility_permissions_ContainerId",
                table: "container_visibility_permissions",
                column: "ContainerId");

            migrationBuilder.CreateIndex(
                name: "IX_container_visibility_permissions_UserId_ServerId",
                table: "container_visibility_permissions",
                columns: new[] { "UserId", "ServerId" });

            migrationBuilder.CreateIndex(
                name: "IX_server_visibility_permissions_ServerId",
                table: "server_visibility_permissions",
                column: "ServerId");
        }
    }
}
