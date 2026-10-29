using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Domain.Access;

namespace UptimeHarbor.Infrastructure.Persistence.Repositories;

// Совместимость вызовов до этапа 05; ресурсные разрешения больше не хранятся.
public sealed class VisibilityPermissionRepository : IVisibilityPermissionRepository
{
    public Task<IReadOnlyList<ServerVisibilityPermission>> GetServerPermissionsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ServerVisibilityPermission>>([]);
    }

    public Task<IReadOnlyList<ContainerVisibilityPermission>> GetContainerPermissionsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ContainerVisibilityPermission>>([]);
    }

    public void Create(ServerVisibilityPermission item)
    {
        throw new NotSupportedException("Resource assignments are not supported.");
    }

    public void Create(ContainerVisibilityPermission item)
    {
        throw new NotSupportedException("Resource assignments are not supported.");
    }

    public void CreateRange(IEnumerable<ServerVisibilityPermission> entities)
    {
        if (entities.Any())
        {
            throw new NotSupportedException("Resource assignments are not supported.");
        }
    }

    public void CreateRange(IEnumerable<ContainerVisibilityPermission> entities)
    {
        if (entities.Any())
        {
            throw new NotSupportedException("Resource assignments are not supported.");
        }
    }

    public void Delete(ServerVisibilityPermission item)
    {
        // В целевой схеме назначений нет.
    }

    public void Delete(ContainerVisibilityPermission item)
    {
        // В целевой схеме назначений нет.
    }

    public void DeleteRange(IEnumerable<ServerVisibilityPermission> entities)
    {
        // Совместимая очистка не имеет хранимых назначений.
    }

    public void DeleteRange(IEnumerable<ContainerVisibilityPermission> entities)
    {
        // Совместимая очистка не имеет хранимых назначений.
    }
}
