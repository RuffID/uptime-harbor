namespace UptimeHarbor.Application.Visibility;

public static class ObjectVisibilityPolicy
{
    public static bool CanAccessServer(
        bool isAdministrator,
        Guid serverId,
        IReadOnlySet<Guid> permittedServerIds)
    {
        ArgumentNullException.ThrowIfNull(permittedServerIds);
        return isAdministrator;
    }

    public static bool CanAccessContainer(
        bool isAdministrator,
        Guid? serverId,
        Guid containerId,
        IReadOnlySet<Guid> permittedServerIds,
        IReadOnlySet<Guid> permittedContainerIds)
    {
        ArgumentNullException.ThrowIfNull(permittedServerIds);
        ArgumentNullException.ThrowIfNull(permittedContainerIds);
        return isAdministrator;
    }
}
