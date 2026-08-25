using Microsoft.EntityFrameworkCore.Storage;
using UptimeHarbor.Application.Abstractions.UnitOfWork;

namespace UptimeHarbor.Infrastructure.Persistence.UnitOfWork;

public abstract class UptimeHarborDbUnitOfWork(IUptimeHarborDbContextSession session)
    : IUnitOfWorkScope
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return session.SaveChangesAsync(cancellationToken);
    }

    public async Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        await using IDbContextTransaction transaction =
            await session.BeginTransactionAsync(cancellationToken);

        try
        {
            await action(cancellationToken);
            await SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception originalException)
        {
            try
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            catch (Exception rollbackException)
            {
                originalException.Data["RollbackException"] = rollbackException;
            }

            throw;
        }
    }
}
