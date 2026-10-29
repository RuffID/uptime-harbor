namespace UptimeHarbor.Domain.Notifications;

public class TelegramChat
{
    private TelegramChat()
    {
    }

    public TelegramChat(Guid id, long telegramChatId, string name)
    {
        EnsureValidId(id);

        if (telegramChatId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(telegramChatId),
                "Идентификатор Telegram-чата не может быть равен нулю.");
        }

        Id = id;
        TelegramChatId = telegramChatId;
        Name = ValidateName(name);
    }

    public Guid Id { get; private set; }

    public long TelegramChatId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public long ConfigRevision { get; private set; }

    public void AcceptConfigurationRevision(long revision)
    {
        if (revision <= ConfigRevision)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        ConfigRevision = revision;
    }

    public void Update(long telegramChatId, string name)
    {
        if (telegramChatId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(telegramChatId),
                "Идентификатор Telegram-чата не может быть равен нулю.");
        }

        string validatedName = ValidateName(name);
        TelegramChatId = telegramChatId;
        Name = validatedName;
    }

    private static void EnsureValidId(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Идентификатор не может быть пустым.", nameof(id));
        }
    }

    private static string ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string value = name.Trim();

        if (value.Length > ModelConstraints.TELEGRAM_CHAT_NAME_MAX_LENGTH)
        {
            throw new ArgumentException("Название Telegram-чата превышает допустимую длину.", nameof(name));
        }

        return value;
    }
}
