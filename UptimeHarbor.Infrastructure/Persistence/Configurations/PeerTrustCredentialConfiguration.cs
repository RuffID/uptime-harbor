using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class PeerTrustCredentialConfiguration : ProjectionConfiguration<PeerTrustCredential>
{
    protected override string TableName { get; } = "peer_trust_credentials";

    public override void Configure(EntityTypeBuilder<PeerTrustCredential> builder)
    {
        base.Configure(builder);
        Digest(builder.Property(entity => entity.Fingerprint));
        builder.Property(entity => entity.PublicCertificate).HasMaxLength(16384).IsRequired();
        builder.HasIndex(entity => entity.Fingerprint).IsUnique();
        builder.HasIndex(entity => entity.NodeId);
        builder.Property(entity => entity.Purpose).HasConversion<string>().HasMaxLength(16);
        builder.HasIndex(entity => entity.AdmissionId).IsUnique().HasFilter("[AdmissionId] IS NOT NULL");
        builder.ToTable(TableName, table => table.HasCheckConstraint("ck_peer_trust_credentials_purpose",
            "[Purpose] IN ('Admission','Active','Retired')"));
        // Публичная история доверия переживает удаление узла; приватных ключей здесь нет.
    }
}
