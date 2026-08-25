using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Domain.Notifications;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class NotificationBindingConfiguration : IEntityTypeConfiguration<NotificationBinding>
{
    public void Configure(EntityTypeBuilder<NotificationBinding> builder)
    {
        builder.ToTable(
            "notification_bindings",
            table => table.HasCheckConstraint(
                "ck_notification_bindings_single_target",
                "(\"ServerId\" IS NOT NULL) <> (\"ContainerId\" IS NOT NULL)"));
        builder.HasKey(binding => binding.Id);
        builder.Property(binding => binding.Id).ValueGeneratedNever();
        builder.HasOne<TelegramChat>()
            .WithMany()
            .HasForeignKey(binding => binding.TelegramChatId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<MonitoredServer>()
            .WithMany()
            .HasForeignKey(binding => binding.ServerId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<MonitoredContainer>()
            .WithMany()
            .HasForeignKey(binding => binding.ContainerId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(binding => new { binding.TelegramChatId, binding.ServerId })
            .IsUnique()
            .HasFilter("\"ServerId\" IS NOT NULL");
        builder.HasIndex(binding => new { binding.TelegramChatId, binding.ContainerId })
            .IsUnique()
            .HasFilter("\"ContainerId\" IS NOT NULL");
    }
}
