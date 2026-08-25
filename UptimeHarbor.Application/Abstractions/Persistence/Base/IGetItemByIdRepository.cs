namespace UptimeHarbor.Application.Abstractions.Persistence.Base;

public interface IGetItemByIdRepository<TEntity, in TId>
{
    Task<TEntity?> GetItemByIdAsync(
        TId id,
        CancellationToken cancellationToken = default);
}
