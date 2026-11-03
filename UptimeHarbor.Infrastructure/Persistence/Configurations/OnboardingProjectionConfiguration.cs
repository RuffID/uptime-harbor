using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class OnboardingProjectionConfiguration : ProjectionConfiguration<OnboardingProjection>
{
    protected override string TableName { get; } = "onboarding_invitations";

    public override void Configure(EntityTypeBuilder<OnboardingProjection> builder)
    {
        base.Configure(builder);
        builder.Property(item => item.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        Payload(builder.Property(item => item.Payload));
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_onboarding_invitations_version", "[Version] = 1");
            table.HasCheckConstraint("ck_onboarding_invitations_status", "[Status] IN ('Unused','Reserved','Consumed','Revoked','Expired')");
        });
    }
}
