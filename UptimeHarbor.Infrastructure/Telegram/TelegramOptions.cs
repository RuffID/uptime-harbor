using System.Globalization;

namespace UptimeHarbor.Infrastructure.Telegram;

public class TelegramOptions
{
    public const string SECTION_NAME = "Telegram";

    public string BotToken { get; init; } = string.Empty;

    public bool HasValidBotToken()
    {
        if (string.IsNullOrWhiteSpace(BotToken))
        {
            return false;
        }

        string[] parts = BotToken.Split(':');
        if (parts.Length != 2)
        {
            return false;
        }

        bool validBotId = long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long botId)
            && botId > 0;
        string secret = parts[1];
        bool validSecret = secret.Length is >= 30 and <= 128
            && secret.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
        return validBotId && validSecret;
    }
}
