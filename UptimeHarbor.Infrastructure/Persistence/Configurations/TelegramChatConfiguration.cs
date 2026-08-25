using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UptimeHarbor.Domain;
using UptimeHarbor.Domain.Notifications;

namespace UptimeHarbor.Infrastructure.Persistence.Configurations;

public class TelegramChatConfiguration : IEntityTypeConfiguration<TelegramChat>
{
    public void Configure(EntityTypeBuilder<TelegramChat> builder)
    {
        builder.ToTable("telegram_chats");
        builder.HasKey(chat => chat.Id);
        builder.Property(chat => chat.Id).ValueGeneratedNever();
        builder.Property(chat => chat.Name)
            .HasMaxLength(ModelConstraints.TELEGRAM_CHAT_NAME_MAX_LENGTH)
            .IsRequired();
        builder.HasIndex(chat => chat.TelegramChatId).IsUnique();
    }
}
