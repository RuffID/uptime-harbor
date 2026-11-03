using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class EnrollmentIssuerConfiguration : ProjectionConfiguration<EnrollmentIssuer>
{
    protected override string TableName { get; } = "enrollment_issuers";

    public override void Configure(EntityTypeBuilder<EnrollmentIssuer> builder)
    {
        base.Configure(builder);
        builder.Property(item => item.Purpose).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(item => item.PublicCertificate).HasMaxLength(16384).IsRequired();
        Digest(builder.Property(item => item.Fingerprint));
        Digest(builder.Property(item => item.SpkiDigest));
        builder.HasIndex(item => item.Fingerprint).IsUnique();
        builder.HasIndex(item => item.SpkiDigest).IsUnique();
        builder.HasIndex(item => new { item.NodeId, item.StorageGeneration });
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_enrollment_issuers_purpose",
                "[Purpose] = 'UptimeHarbor.EnrollmentPeer.v1'");
            table.HasCheckConstraint("ck_enrollment_issuers_lifetime",
                "[NotAfterUtc] > [NotBeforeUtc] AND [NotAfterUtc] <= DATEADD(year, 3, [NotBeforeUtc])");
            table.HasCheckConstraint("ck_enrollment_issuers_transitions",
                "([RetiredAtUtc] IS NULL OR [RetiredAtUtc] >= [NotBeforeUtc]) AND " +
                "([RevokedAtUtc] IS NULL OR [RevokedAtUtc] >= [NotBeforeUtc])");
        });
        // История issuer переживает удаление узла. Identity/epoch проверяются accepted reducer, не cascade delete.
    }
}
