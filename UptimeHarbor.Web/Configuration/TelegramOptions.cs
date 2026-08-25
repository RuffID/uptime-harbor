namespace UptimeHarbor.Web.Configuration;

public class TelegramOptions
{
    public const string SECTION_NAME = "Telegram";

    public string BotToken { get; init; } = string.Empty;
}
