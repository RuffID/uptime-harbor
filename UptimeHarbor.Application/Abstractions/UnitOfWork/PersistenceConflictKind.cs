namespace UptimeHarbor.Application.Abstractions.UnitOfWork;

public enum PersistenceConflictKind
{
    DuplicateTelegramChatId,
    DuplicateNotificationBinding
}
