using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class RecoveryEntitlementConfiguration : ProjectionConfiguration<RecoveryEntitlement>
{
    protected override string TableName { get; } = "recovery_entitlements";

    public override void Configure(EntityTypeBuilder<RecoveryEntitlement> builder)
    {
        base.Configure(builder);
        builder.HasIndex(entity => new { entity.IncidentId, entity.ChatId }).IsUnique();
        builder.HasOne<SharedIncident>().WithMany().HasForeignKey(entity => entity.IncidentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DeliveryIntent>().WithMany().HasForeignKey(entity => entity.ProblemIntentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DeliveryIntent>().WithMany().HasForeignKey(entity => entity.RecoveryIntentId).OnDelete(DeleteBehavior.Restrict);
    }
}
