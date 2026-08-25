namespace UptimeHarbor.Application.Abstractions.Persistence.Base;

public interface IDeleteItemRepository<TEntity>
{
    void Delete(TEntity item);

    void DeleteRange(IEnumerable<TEntity> entities);
}
