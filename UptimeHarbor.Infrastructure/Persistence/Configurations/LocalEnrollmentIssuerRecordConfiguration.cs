using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class LocalEnrollmentIssuerRecordConfiguration : IEntityTypeConfiguration<LocalEnrollmentIssuerRecord>
{
    public void Configure(EntityTypeBuilder<LocalEnrollmentIssuerRecord> builder)
    {
        builder.ToTable("local_enrollment_issuers", table =>
        {
            table.HasCheckConstraint("ck_local_enrollment_issuers_generation", "[ClusterGeneration] > 0");
            table.HasCheckConstraint("ck_local_enrollment_issuers_der", "DATALENGTH([CertificateDer]) BETWEEN 1 AND 65536");
        });
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();
        builder.Property(item => item.KeyReference).HasMaxLength(97).IsUnicode(false).IsRequired();
        builder.Property(item => item.CertificateDer).HasMaxLength(65536).IsRequired();
        builder.Property(item => item.CertificateDigest).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(item => item.SpkiDigest).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        builder.HasIndex(item => item.KeyReference).IsUnique();
        builder.HasIndex(item => item.CertificateDigest).IsUnique();
        builder.HasIndex(item => item.SpkiDigest).IsUnique();
        builder.HasIndex(item => new { item.ClusterId, item.ClusterGeneration });
    }
}
