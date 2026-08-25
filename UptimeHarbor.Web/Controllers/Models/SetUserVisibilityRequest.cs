namespace UptimeHarbor.Web.Controllers.Models;

public class SetUserVisibilityRequest
{
    public IReadOnlyList<Guid> ServerIds { get; init; } = [];

    public IReadOnlyList<Guid> ContainerIds { get; init; } = [];
}
