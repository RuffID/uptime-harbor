namespace UptimeHarbor.Application.Abstractions.UnitOfWork;

public enum PersistenceConflictKind
{
    DuplicateServerName,
    DuplicateTelegramChatId,
    DuplicateNotificationBinding
}
