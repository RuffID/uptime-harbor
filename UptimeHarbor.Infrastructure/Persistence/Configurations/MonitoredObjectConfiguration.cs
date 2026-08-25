using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain;
using UptimeHarbor.Domain.Monitoring;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public abstract class MonitoredObjectConfiguration<TEntity> : IEntityTypeConfiguration<TEntity>
    where TEntity : MonitoredObject
{
    public virtual void Configure(EntityTypeBuilder<TEntity> builder)
    {
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.EndpointUrl)
            .HasMaxLength(ModelConstraints.ENDPOINT_URL_MAX_LENGTH)
            .IsRequired();
        builder.Property(entity => entity.Description)
            .HasMaxLength(ModelConstraints.DESCRIPTION_MAX_LENGTH);
        builder.Property(entity => entity.LastKnownName)
            .HasMaxLength(ModelConstraints.OBJECT_NAME_MAX_LENGTH);
        builder.Property(entity => entity.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(entity => entity.LastCheckedAtUtc)
            .HasColumnType("timestamp with time zone");
        builder.Property(entity => entity.LastSuccessfulCheckAtUtc)
            .HasColumnType("timestamp with time zone");
        builder.Property(entity => entity.CurrentIncidentId);
    }
}
