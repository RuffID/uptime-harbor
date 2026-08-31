using Microsoft.EntityFrameworkCore;

namespace UptimeHarbor.Infrastructure.Services.DataBase;

public interface IBackupService<TContext>
    where TContext : DbContext
{
    Task CreateSqlServerBackupAsync(CancellationToken cancellationToken = default);
}
