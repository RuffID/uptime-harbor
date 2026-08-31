using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using UptimeHarbor.Application.Abstractions.UnitOfWork;

namespace UptimeHarbor.Infrastructure.Persistence.UnitOfWork;

public abstract class UptimeHarborDbUnitOfWork(IUptimeHarborDbContextSession session)
    : IUnitOfWorkScope
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await session.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            session.Clear();
            throw new PersistenceConcurrencyException(exception);
        }
        catch (DbUpdateException exception)
        {
            if (!SqlServerPersistenceConflictClassifier.TryClassify(exception, out PersistenceConflictKind kind))
            {
                throw;
            }

            session.Clear();
            throw new PersistenceConflictException(kind, exception);
        }
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
