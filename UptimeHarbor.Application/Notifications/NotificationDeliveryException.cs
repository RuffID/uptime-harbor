namespace UptimeHarbor.Application.Notifications;

public class NotificationDeliveryException : Exception
{
    public NotificationDeliveryException(string message)
        : base(message)
    {
    }
}
