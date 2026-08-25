namespace UptimeHarbor.Application.Abstractions.UnitOfWork;

public interface IUnitOfWorkScope
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default);
}
