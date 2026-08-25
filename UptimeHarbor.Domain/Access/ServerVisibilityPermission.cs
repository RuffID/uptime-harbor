namespace UptimeHarbor.Domain.Access;

public class ServerVisibilityPermission
{
    private ServerVisibilityPermission()
    {
    }

    public ServerVisibilityPermission(Guid userId, Guid serverId)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("Идентификатор пользователя не может быть пустым.", nameof(userId));
        }

        if (serverId == Guid.Empty)
        {
            throw new ArgumentException("Идентификатор сервера не может быть пустым.", nameof(serverId));
        }

        UserId = userId;
        ServerId = serverId;
    }

    public Guid UserId { get; private set; }

    public Guid ServerId { get; private set; }
}
