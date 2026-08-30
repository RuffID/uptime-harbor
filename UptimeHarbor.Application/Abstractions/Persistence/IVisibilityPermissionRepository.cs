using UptimeHarbor.Application.Abstractions.Persistence.Base;
using UptimeHarbor.Domain.Access;

namespace UptimeHarbor.Application.Abstractions.Persistence;

public interface IVisibilityPermissionRepository :
    ICreateItemRepository<ServerVisibilityPermission>,
    ICreateItemRepository<ContainerVisibilityPermission>,
    IDeleteItemRepository<ServerVisibilityPermission>,
    IDeleteItemRepository<ContainerVisibilityPermission>
{
    Task<IReadOnlyList<ServerVisibilityPermission>> GetServerPermissionsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContainerVisibilityPermission>> GetContainerPermissionsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

}
