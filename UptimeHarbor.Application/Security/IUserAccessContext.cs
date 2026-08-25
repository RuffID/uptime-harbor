namespace UptimeHarbor.Application.Security;

public interface IUserAccessContext
{
    bool IsAuthenticated { get; }

    bool IsAdministrator { get; }

    bool IsEnabled { get; }

    Guid UserId { get; }
}
