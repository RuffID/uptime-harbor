namespace UptimeHarbor.Application.Notifications;

public class NotificationDeliveryException : Exception
{
    public NotificationDeliveryException(string message)
        : base(message)
    {
    }

    public NotificationDeliveryException(NotificationDeliveryFailure failure, int? retryAfterSeconds = null)
        : base(GetUserMessage(failure))
    {
        Failure = failure;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public NotificationDeliveryFailure Failure { get; }
    public int? RetryAfterSeconds { get; }

    public string UserMessage
    {
        get { return GetUserMessage(Failure); }
    }

    private static string GetUserMessage(NotificationDeliveryFailure failure)
    {
        switch (failure)
        {
            case NotificationDeliveryFailure.InvalidConfiguration:
                return "В Config/config.json не настроен корректный Telegram:BotToken. Замените заглушку на token, выданный BotFather, и перезапустите приложение.";
            case NotificationDeliveryFailure.InvalidToken:
                return "Telegram отклонил token бота. Проверьте Telegram:BotToken в Config/config.json: token мог быть неверным или отозванным.";
            case NotificationDeliveryFailure.InvalidChat:
                return "Telegram отклонил сообщение. Проверьте ID чата и право бота отправлять в него сообщения.";
            case NotificationDeliveryFailure.ChatNotFound:
                return "Telegram не нашёл чат. Проверьте ID чата; для личного чата сначала нажмите «Старт» у бота, для группы — добавьте в неё бота.";
            case NotificationDeliveryFailure.Forbidden:
                return "Боту запрещена отправка в этот чат. Проверьте, что бот не заблокирован, добавлен в группу и имеет право отправлять сообщения.";
            case NotificationDeliveryFailure.RateLimited:
                return "Telegram ограничил частоту отправки. Повторите попытку позже.";
            case NotificationDeliveryFailure.Unavailable:
                return "Telegram временно недоступен. Повторите попытку позже.";
            case NotificationDeliveryFailure.Timeout:
                return "Telegram не ответил вовремя. Проверьте соединение сервера с Telegram и повторите попытку.";
            case NotificationDeliveryFailure.Network:
                return "Не удалось подключиться к Telegram. Проверьте доступ сервера к api.telegram.org.";
            case NotificationDeliveryFailure.InvalidResponse:
                return "Получен некорректный ответ Telegram. Проверьте соединение сервера с Telegram и повторите попытку.";
            case NotificationDeliveryFailure.Unknown:
                return "Не удалось отправить сообщение в Telegram. Проверьте настройки бота и чата.";
            default:
                throw new ArgumentOutOfRangeException(nameof(failure), failure, "Неизвестная причина ошибки доставки.");
        }
    }
}
