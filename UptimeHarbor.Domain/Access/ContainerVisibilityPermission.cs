namespace UptimeHarbor.Domain.Access;

public class ContainerVisibilityPermission
{
    private ContainerVisibilityPermission()
    {
    }

    public ContainerVisibilityPermission(Guid userId, Guid serverId, Guid containerId)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("Идентификатор пользователя не может быть пустым.", nameof(userId));
        }

        if (serverId == Guid.Empty)
        {
            throw new ArgumentException("Идентификатор сервера не может быть пустым.", nameof(serverId));
        }

        if (containerId == Guid.Empty)
        {
            throw new ArgumentException("Идентификатор контейнера не может быть пустым.", nameof(containerId));
        }

        UserId = userId;
        ServerId = serverId;
        ContainerId = containerId;
    }

    public Guid UserId { get; private set; }

    public Guid ServerId { get; private set; }

    public Guid ContainerId { get; private set; }
}
