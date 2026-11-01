using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class PendingOperationConfiguration : IEntityTypeConfiguration<PendingOperation>
{
    public void Configure(EntityTypeBuilder<PendingOperation> builder)
    {
        builder.ToTable("local_pending_operations", table =>
        {
            table.HasCheckConstraint("ck_local_pending_operations_actor",
                "([ActorUserId] IS NOT NULL AND [SystemSource] IS NULL) OR ([ActorUserId] IS NULL AND [SystemSource] IS NOT NULL)");
            table.HasCheckConstraint("ck_local_pending_operations_state",
                "[State] IN ('LocalPending','AwaitingDecision','NeedsUserConfirmation','Resolved')");
            table.HasCheckConstraint("ck_local_pending_operations_versions",
                "[ClusterGeneration] > 0 AND [BaseRevision] >= 0 AND [ActorUserRevision] >= 0 AND [SubmissionAttempts] >= 0");
        });
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.Digest).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(entity => entity.ProtectedPayload).HasMaxLength(StorageGuards.MAX_PAYLOAD_BYTES).IsRequired();
        builder.Property(entity => entity.Kind).HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.SystemSource).HasMaxLength(64);
        builder.Property(entity => entity.State).HasConversion<string>().HasMaxLength(32).IsConcurrencyToken();
        builder.Property(entity => entity.RequestedControlKind).HasConversion<string>().HasMaxLength(16);
        builder.HasIndex(entity => new { entity.State, entity.NextAttemptAtUtc });
    }
}
