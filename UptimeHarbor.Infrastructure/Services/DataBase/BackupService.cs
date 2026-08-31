using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace UptimeHarbor.Infrastructure.Services.DataBase;

public class BackupService<TContext>(
    string connectionString,
    string backupFolder,
    ILogger<BackupService<TContext>> logger,
    IBackupFilePathBuilder backupFilePathBuilder)
    : IBackupService<TContext>
    where TContext : DbContext
{
    public async Task CreateSqlServerBackupAsync(
        CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(backupFolder);
        }

        string backupFilePath = backupFilePathBuilder.Build(backupFolder);
        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);

        string databaseName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:SqlServer must contain a database name.");
        }

        string escapedDatabaseName = databaseName.Replace("]", "]]", StringComparison.Ordinal);
        string escapedBackupFilePath = backupFilePath.Replace("'", "''", StringComparison.Ordinal);
        string commandText =
            $"BACKUP DATABASE [{escapedDatabaseName}] " +
            $"TO DISK = N'{escapedBackupFilePath}' " +
            "WITH FORMAT, INIT, NAME = 'UptimeHarbor startup backup';";

        await using SqlCommand command = new(commandText, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);

        logger.LogInformation("SQL Server backup was created before migration.");
    }
}
