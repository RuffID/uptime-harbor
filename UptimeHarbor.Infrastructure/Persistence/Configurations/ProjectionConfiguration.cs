using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public abstract class ProjectionConfiguration<TEntity> : IEntityTypeConfiguration<TEntity>
    where TEntity : ProjectionRecord
{
    protected abstract string TableName { get; }

    public virtual void Configure(EntityTypeBuilder<TEntity> builder)
    {
        builder.HasBaseType((Type?)null);
        builder.ToTable(TableName, table => table.HasCheckConstraint($"ck_{TableName}_position",
            "[ClusterGeneration] > 0 AND [LogIndex] > 0 AND [Term] > 0"));
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.LogIndex).IsConcurrencyToken();
        builder.HasIndex(entity => new { entity.ClusterId, entity.ClusterGeneration, entity.LogIndex });
    }

    protected static void Payload(PropertyBuilder<string> property)
    {
        property.HasMaxLength(StorageGuards.MAX_PAYLOAD_BYTES).IsRequired();
    }

    protected static void Digest(PropertyBuilder<string> property)
    {
        property.HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
    }
}
