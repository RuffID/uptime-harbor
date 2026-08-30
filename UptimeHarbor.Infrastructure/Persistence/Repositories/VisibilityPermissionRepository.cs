using EFCoreLibrary.Abstractions.Database.Repository.Base;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Domain.Access;

namespace UptimeHarbor.Infrastructure.Persistence.Repositories;

public class VisibilityPermissionRepository(
    IGetItemByPredicateRepository<ServerVisibilityPermission, UptimeHarborDbContext> serverGetRepository,
    IGetItemByPredicateRepository<ContainerVisibilityPermission, UptimeHarborDbContext> containerGetRepository,
    ICreateItemRepository<ServerVisibilityPermission, UptimeHarborDbContext> serverCreateRepository,
    ICreateItemRepository<ContainerVisibilityPermission, UptimeHarborDbContext> containerCreateRepository,
    IDeleteItemRepository<ServerVisibilityPermission, UptimeHarborDbContext> serverDeleteRepository,
    IDeleteItemRepository<ContainerVisibilityPermission, UptimeHarborDbContext> containerDeleteRepository)
    : IVisibilityPermissionRepository
{
    public Task<List<ServerVisibilityPermission>> GetServerPermissionsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return serverGetRepository.GetItemsByPredicateAsync(
            permission => permission.UserId == userId,
            asNoTracking: true,
            ct: cancellationToken);
    }

    public Task<List<ContainerVisibilityPermission>> GetContainerPermissionsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return containerGetRepository.GetItemsByPredicateAsync(
            permission => permission.UserId == userId,
            asNoTracking: true,
            ct: cancellationToken);
    }

    public Task<List<ServerVisibilityPermission>> GetServerPermissionsForUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 0)
        {
            return Task.FromResult<List<ServerVisibilityPermission>>([]);
        }

        return serverGetRepository.GetItemsByPredicateAsync(
            permission => userIds.Contains(permission.UserId),
            asNoTracking: true,
            ct: cancellationToken);
    }

    public Task<List<ContainerVisibilityPermission>> GetContainerPermissionsForUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 0)
        {
            return Task.FromResult<List<ContainerVisibilityPermission>>([]);
        }

        return containerGetRepository.GetItemsByPredicateAsync(
            permission => userIds.Contains(permission.UserId),
            asNoTracking: true,
            ct: cancellationToken);
    }

    public void Create(ServerVisibilityPermission item)
    {
        serverCreateRepository.Create(item);
    }

    public void CreateRange(IEnumerable<ServerVisibilityPermission> entities)
    {
        serverCreateRepository.CreateRange(entities);
    }

    public void Create(ContainerVisibilityPermission item)
    {
        containerCreateRepository.Create(item);
    }

    public void CreateRange(IEnumerable<ContainerVisibilityPermission> entities)
    {
        containerCreateRepository.CreateRange(entities);
    }

    public void Delete(ServerVisibilityPermission item)
    {
        serverDeleteRepository.Delete(item);
    }

    public void DeleteRange(IEnumerable<ServerVisibilityPermission> entities)
    {
        serverDeleteRepository.DeleteRange(entities);
    }

    public void Delete(ContainerVisibilityPermission item)
    {
        containerDeleteRepository.Delete(item);
    }

    public void DeleteRange(IEnumerable<ContainerVisibilityPermission> entities)
    {
        containerDeleteRepository.DeleteRange(entities);
    }
}
