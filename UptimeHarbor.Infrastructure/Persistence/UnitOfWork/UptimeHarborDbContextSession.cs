using Microsoft.EntityFrameworkCore.Storage;

namespace UptimeHarbor.Infrastructure.Persistence.UnitOfWork;

public class UptimeHarborDbContextSession(UptimeHarborDbContext dbContext)
    : IUptimeHarborDbContextSession
{
    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        return dbContext.Database.BeginTransactionAsync(cancellationToken);
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        return dbContext.SaveChangesAsync(cancellationToken);
    }

    public void Clear()
    {
        dbContext.ChangeTracker.Clear();
    }
}
