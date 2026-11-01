using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class DeliveryIntentConfiguration : ProjectionConfiguration<DeliveryIntent>
{
    protected override string TableName { get; } = "delivery_intents";

    public override void Configure(EntityTypeBuilder<DeliveryIntent> builder)
    {
        base.Configure(builder);
        builder.Property(entity => entity.Kind).HasConversion<string>().HasMaxLength(16);
        builder.Property(entity => entity.State).HasConversion<string>().HasMaxLength(32).IsConcurrencyToken();
        builder.Property(entity => entity.LogicalKey).HasMaxLength(256).IsRequired();
        Payload(builder.Property(entity => entity.ProtectedPayload));
        builder.HasIndex(entity => entity.LogicalKey).IsUnique();
        builder.HasIndex(entity => new { entity.EventId, entity.Kind, entity.ChatId }).IsUnique();
        builder.HasIndex(entity => new { entity.IncidentId, entity.Kind, entity.ChatId }).IsUnique()
            .HasFilter("[IncidentId] IS NOT NULL AND [Kind] IN ('Problem','Recovery')");
        builder.HasIndex(entity => new { entity.State, entity.NextAttemptAtUtc });
        builder.HasOne<IncidentEvent>().WithMany().HasForeignKey(entity => new { entity.EventId, entity.IncidentId })
            .HasPrincipalKey(entity => new { entity.Id, entity.IncidentId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DeliverySummary>().WithMany().HasForeignKey(entity => entity.SummaryId).OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_delivery_intents_attempts", "[AttemptCount] BETWEEN 0 AND 5");
            table.HasCheckConstraint("ck_delivery_intents_kind", "[Kind] IN ('Problem','Recovery','Update','Summary','Test')");
            table.HasCheckConstraint("ck_delivery_intents_event",
                "([Kind] IN ('Test','Summary') AND [IncidentId] IS NULL AND [EventId] IS NULL) OR ([Kind] IN ('Problem','Recovery','Update') AND [IncidentId] IS NOT NULL AND [EventId] IS NOT NULL)");
            table.HasCheckConstraint("ck_delivery_intents_actor",
                "([Kind] = 'Test' AND [ActorUserId] IS NOT NULL) OR ([Kind] <> 'Test' AND [ActorUserId] IS NULL)");
            table.HasCheckConstraint("ck_delivery_intents_state", "[State] IN ('Planned','AttemptStarted','Succeeded','RetryDue','Failed','Cancelled','SuppressedBySummary')");
        });
    }
}
