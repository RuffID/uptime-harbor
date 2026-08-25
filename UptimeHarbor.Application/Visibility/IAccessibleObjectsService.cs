using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Visibility.Models;

namespace UptimeHarbor.Application.Visibility;

public interface IAccessibleObjectsService
{
    Task<ServiceResult<IReadOnlyList<AccessibleServerDto>>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<ServiceResult<AccessibleServerDto>> GetServerAsync(Guid serverId, CancellationToken cancellationToken = default);

    Task<ServiceResult<AccessibleContainerDto>> GetContainerAsync(Guid containerId, CancellationToken cancellationToken = default);

    Task<bool> CanAccessServerAsync(Guid serverId, CancellationToken cancellationToken = default);

    Task<bool> CanAccessContainerAsync(Guid containerId, CancellationToken cancellationToken = default);
}
