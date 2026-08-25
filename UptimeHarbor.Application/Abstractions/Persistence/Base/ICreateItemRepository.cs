namespace UptimeHarbor.Application.Abstractions.Persistence.Base;

public interface ICreateItemRepository<TEntity>
{
    void Create(TEntity item);

    void CreateRange(IEnumerable<TEntity> entities);
}
