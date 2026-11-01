namespace UptimeHarbor.Application.Users.Models;

public record UserAccountDto(
    Guid Id,
    string Login,
    bool IsEnabled,
    bool IsAdministrator,
    IReadOnlyList<Guid> ServerIds,
    IReadOnlyList<Guid> ContainerIds)
{
    public long Revision { get; init; }
}
