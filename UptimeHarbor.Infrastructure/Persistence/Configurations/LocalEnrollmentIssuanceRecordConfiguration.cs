using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class LocalEnrollmentIssuanceRecordConfiguration : IEntityTypeConfiguration<LocalEnrollmentIssuanceRecord>
{
    public void Configure(EntityTypeBuilder<LocalEnrollmentIssuanceRecord> builder)
    {
        builder.ToTable("local_enrollment_issuances", table =>
        {
            table.HasCheckConstraint("ck_local_enrollment_issuances_authorization",
                "([GenesisId] IS NULL AND [AuthorizationIndex] > 0) OR " +
                "([GenesisId] IS NOT NULL AND [GenesisId] <> '00000000-0000-0000-0000-000000000000' AND [AuthorizationIndex] = 0)");
            table.HasCheckConstraint("ck_local_enrollment_issuances_der", "DATALENGTH([CertificateDer]) BETWEEN 1 AND 65536");
        });
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();
        builder.Property(item => item.CertificateDer).HasMaxLength(65536).IsRequired();
        builder.Property(item => item.CertificateDigest).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(item => item.RequestDigest).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(item => item.TemplateDigest).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        builder.HasIndex(item => item.CertificateDigest).IsUnique();
        builder.HasIndex(item => item.AttemptId);
        builder.HasOne<LocalEnrollmentIssuerRecord>().WithMany().HasForeignKey(item => item.IssuerEpoch)
            .OnDelete(DeleteBehavior.Restrict);
        // До Bind/Admit нет Node/OperationalCredential; orphan DER не становится peer authority.
    }
}
