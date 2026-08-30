using UptimeHarbor.Application.Abstractions.Persistence.Base;
using UptimeHarbor.Domain.Access;

namespace UptimeHarbor.Application.Abstractions.Persistence;

public interface IVisibilityPermissionRepository :
    ICreateItemRepository<ServerVisibilityPermission>,
    ICreateItemRepository<ContainerVisibilityPermission>,
    IDeleteItemRepository<ServerVisibilityPermission>,
    IDeleteItemRepository<ContainerVisibilityPermission>
{
    Task<List<ServerVisibilityPermission>> GetServerPermissionsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<List<ContainerVisibilityPermission>> GetContainerPermissionsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<List<ServerVisibilityPermission>> GetServerPermissionsForUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken = default);

    Task<List<ContainerVisibilityPermission>> GetContainerPermissionsForUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken = default);
}
