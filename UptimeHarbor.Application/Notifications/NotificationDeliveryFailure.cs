namespace UptimeHarbor.Application.Notifications;

public enum NotificationDeliveryFailure
{
    Unknown,
    InvalidConfiguration,
    InvalidToken,
    InvalidChat,
    ChatNotFound,
    Forbidden,
    RateLimited,
    Unavailable,
    Timeout,
    Network,
    InvalidResponse
}
