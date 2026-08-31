using EFCoreLibrary.Abstractions.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace UptimeHarbor.Infrastructure.Services.DataBase;

public class DataBaseCheckUpService<TContext>(
    IAppDbContext<TContext> dbContext,
    ILogger<DataBaseCheckUpService<TContext>> logger,
    IBackupService<TContext> backupService)
    where TContext : DbContext
{
    public async Task CheckOrUpdateDatabaseAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await dbContext.Database.CanConnectAsync(cancellationToken))
        {
            logger.LogError("Failed to connect to SQL Server during startup.");
            throw new InvalidOperationException(
                "Failed to connect to SQL Server during startup.");
        }

        logger.LogInformation("SQL Server connection was verified during startup.");

        List<string> pendingMigrations = (await dbContext.Database
                .GetPendingMigrationsAsync(cancellationToken))
            .ToList();
        if (pendingMigrations.Count == 0)
        {
            logger.LogInformation("No pending database migrations were found.");
            return;
        }

        foreach (string migration in pendingMigrations)
        {
            logger.LogInformation("Pending database migration: {Migration}.", migration);
        }

        await backupService.CreateSqlServerBackupAsync(cancellationToken);
        await dbContext.Database.MigrateAsync(cancellationToken);
        logger.LogInformation("Database migrations were applied successfully.");
    }
}
