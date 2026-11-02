using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Domain.Notifications;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class NotificationBindingConfiguration : IEntityTypeConfiguration<NotificationBinding>
{
    public const string CHAT_SERVER_UNIQUE_INDEX =
        "IX_notification_bindings_TelegramChatId_ServerId";
    public const string CHAT_CONTAINER_UNIQUE_INDEX =
        "IX_notification_bindings_TelegramChatId_ContainerId";

    public void Configure(EntityTypeBuilder<NotificationBinding> builder)
    {
        builder.ToTable(
            "notification_bindings",
            table => table.HasCheckConstraint(
                "ck_notification_bindings_single_target",
                "([ServerId] IS NOT NULL AND [ContainerId] IS NULL) OR " +
                "([ServerId] IS NULL AND [ContainerId] IS NOT NULL)"));
        builder.HasKey(binding => binding.Id);
        builder.Property(binding => binding.Id).ValueGeneratedNever();
        builder.HasOne<TelegramChat>()
            .WithMany()
            .HasForeignKey(binding => binding.TelegramChatId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<MonitoredServer>()
            .WithMany()
            .HasForeignKey(binding => binding.ServerId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<MonitoredServer>()
            .WithMany()
            .HasForeignKey(binding => binding.ContainerId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(binding => new { binding.TelegramChatId, binding.ServerId })
            .IsUnique()
            .HasDatabaseName(CHAT_SERVER_UNIQUE_INDEX)
            .HasFilter("[ServerId] IS NOT NULL");
        builder.HasIndex(binding => new { binding.TelegramChatId, binding.ContainerId })
            .IsUnique()
            .HasDatabaseName(CHAT_CONTAINER_UNIQUE_INDEX)
            .HasFilter("[ContainerId] IS NOT NULL");
        builder.Property(binding => binding.OutageDeliveredIncidentId);
        builder.Property(binding => binding.RecoveryDeliveredIncidentId);
    }
}
