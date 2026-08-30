using Microsoft.EntityFrameworkCore.Storage;

namespace UptimeHarbor.Infrastructure.Persistence.UnitOfWork;

public interface IUptimeHarborDbContextSession
{
    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    void Clear();
}
