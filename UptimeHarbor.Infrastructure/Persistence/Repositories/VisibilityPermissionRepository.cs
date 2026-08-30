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
    public async Task<IReadOnlyList<ServerVisibilityPermission>> GetServerPermissionsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        List<ServerVisibilityPermission> permissions = await serverGetRepository.GetItemsByPredicateAsync(
            permission => permission.UserId == userId,
            asNoTracking: true,
            ct: cancellationToken);
        return permissions;
    }

    public async Task<IReadOnlyList<ContainerVisibilityPermission>> GetContainerPermissionsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        List<ContainerVisibilityPermission> permissions = await containerGetRepository.GetItemsByPredicateAsync(
            permission => permission.UserId == userId,
            asNoTracking: true,
            ct: cancellationToken);
        return permissions;
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
